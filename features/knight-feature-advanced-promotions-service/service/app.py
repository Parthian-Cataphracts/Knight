"""
Advanced Promotions — a real KNIGHT external service.

The store's own coupons are one code, one discount. This is the engine for the
rest: percentage or fixed off a basket over a threshold, buy-X-get-Y on a
product, and fixed-price bundles — each with a priority, an active window, and a
stackable flag that decides whether a cheaper promotion may pile on after it.

The merchant defines promotions on the screen (staff, proxied here). The
storefront and the checkout send a basket to /api/v1/public/evaluate and get
back the discount and the line-by-line reason, so the same engine prices the
cart on the page and at the till. Nothing here trusts a total the caller sends —
the basket's own line prices are the truth.

Partitioned by X-Knight-Store; every request is the canonical HMAC every
external service verifies.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import sqlite3
import time
import uuid
from contextlib import closing
from datetime import datetime, timezone

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse

VERSION = "2.1.0"
DB_PATH = os.environ.get("ADVANCED_PROMOTIONS_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("ADVANCED_PROMOTIONS_CONTROL_SECRET", "")
SKEW_DEFAULT = 300
TYPES = ("percent_off_cart", "fixed_off_cart", "buy_x_get_y", "bundle")

app = FastAPI(title="KNIGHT Advanced Promotions Service", version=VERSION)


def connect() -> sqlite3.Connection:
    c = sqlite3.connect(DB_PATH)
    c.row_factory = sqlite3.Row
    return c


def init_db() -> None:
    os.makedirs(os.path.dirname(DB_PATH) or ".", exist_ok=True)
    with closing(connect()) as c:
        c.execute(
            "CREATE TABLE IF NOT EXISTS store_secrets (store_id TEXT PRIMARY KEY, secret TEXT NOT NULL, "
            "prev_secret TEXT, prev_expires INTEGER, enabled INTEGER NOT NULL DEFAULT 1, updated_at TEXT NOT NULL)"
        )
        c.execute(
            "CREATE TABLE IF NOT EXISTS promotions (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, name TEXT NOT NULL, "
            "type TEXT NOT NULL, params TEXT NOT NULL DEFAULT '{}', priority INTEGER NOT NULL DEFAULT 0, "
            "stackable INTEGER NOT NULL DEFAULT 1, active INTEGER NOT NULL DEFAULT 1, "
            "starts_at TEXT, ends_at TEXT, created_at TEXT NOT NULL)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_promos ON promotions(store_id, active)")
        c.commit()


init_db()


# ------------------------------------------------------------------- security

def _sign(secret: str, method: str, path: str, ts: str, nonce: str, body: bytes) -> str:
    msg = "\n".join([method.upper(), path, ts, nonce, hashlib.sha256(body).hexdigest()])
    return hmac.new(secret.encode(), msg.encode(), hashlib.sha256).hexdigest()


def _check(request: Request, body: bytes, candidates: list[str]) -> str | None:
    h = request.headers.get("x-knight-signature", "")
    ts = request.headers.get("x-knight-timestamp", "")
    nonce = request.headers.get("x-knight-nonce", "")
    if not h.startswith("sha256=") or not ts or not nonce:
        return "missing or malformed signature headers"
    try:
        skew = int(request.headers.get("x-knight-skew-seconds", SKEW_DEFAULT))
        sent = int(ts)
    except ValueError:
        return "unparseable timestamp or skew"
    if abs(int(time.time()) - sent) > max(skew, 0):
        return "the request is outside the clock-skew window"
    presented = h[len("sha256="):]
    for s in candidates:
        if s and hmac.compare_digest(_sign(s, request.method, request.url.path, ts, nonce, body), presented):
            return None
    return "the signature does not match"


def _store(request: Request) -> str:
    return request.headers.get("x-knight-store", "").strip()


def _candidates(sid: str) -> list[str]:
    with closing(connect()) as c:
        row = c.execute(
            "SELECT secret, prev_secret, prev_expires, enabled FROM store_secrets WHERE store_id=?", (sid,)
        ).fetchone()
    if row is None or not row["enabled"]:
        return []
    out = [row["secret"]]
    if row["prev_secret"] and row["prev_expires"] and int(row["prev_expires"]) > int(time.time()):
        out.append(row["prev_secret"])
    return out


def _verify(request: Request, body: bytes) -> str | None:
    return _check(request, body, _candidates(_store(request)))


def _payload(body: bytes) -> dict:
    try:
        d = json.loads(body) if body else {}
        return d if isinstance(d, dict) else {}
    except json.JSONDecodeError:
        return {}


# ------------------------------------------------------- control-plane calls

@app.post("/knight/stores/register")
async def register(request: Request):
    body = await request.body()
    if (e := _check(request, body, [CONTROL_SECRET])) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = json.loads(body or b"{}")
    sid, secret = str(d.get("storeId") or "").strip(), str(d.get("secret") or "")
    if not sid or not secret:
        return JSONResponse({"error": "storeId and secret required"}, status_code=400)
    with closing(connect()) as c:
        c.execute(
            "INSERT INTO store_secrets(store_id,secret,enabled,updated_at) VALUES(?,?,?,?) "
            "ON CONFLICT(store_id) DO UPDATE SET secret=excluded.secret, enabled=excluded.enabled, updated_at=excluded.updated_at",
            (sid, secret, 1 if d.get("enabled", True) else 0, datetime.now(timezone.utc).isoformat()),
        )
        c.commit()
    return JSONResponse({"registered": True})


@app.post("/knight/stores/rotate")
async def rotate(request: Request):
    body = await request.body()
    if (e := _check(request, body, [CONTROL_SECRET])) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = json.loads(body or b"{}")
    sid, secret = str(d.get("storeId") or "").strip(), str(d.get("secret") or "")
    overlap = int(d.get("overlapSeconds") or 0)
    with closing(connect()) as c:
        cur = c.execute("SELECT secret FROM store_secrets WHERE store_id=?", (sid,)).fetchone()
        prev = cur["secret"] if cur else None
        exp = int(time.time()) + overlap if prev else None
        c.execute(
            "INSERT INTO store_secrets(store_id,secret,prev_secret,prev_expires,enabled,updated_at) VALUES(?,?,?,?,1,?) "
            "ON CONFLICT(store_id) DO UPDATE SET prev_secret=?, prev_expires=?, secret=excluded.secret, enabled=1, updated_at=excluded.updated_at",
            (sid, secret, prev, exp, datetime.now(timezone.utc).isoformat(), prev, exp),
        )
        c.commit()
    return JSONResponse({"rotated": True})


@app.post("/knight/stores/revoke")
async def revoke(request: Request):
    body = await request.body()
    if (e := _check(request, body, [CONTROL_SECRET])) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = str(json.loads(body or b"{}").get("storeId") or "").strip()
    with closing(connect()) as c:
        c.execute("DELETE FROM store_secrets WHERE store_id=?", (sid,))
        c.commit()
    return JSONResponse({"revoked": True})


@app.post("/hooks/{_rest:path}")
async def hook(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"recorded": False, "reason": "promotions are evaluated on demand, not from events"})


# ------------------------------------------------------------- the engine

def _active(c: sqlite3.Connection, sid: str) -> list[sqlite3.Row]:
    now = datetime.now(timezone.utc).isoformat()
    rows = c.execute(
        "SELECT * FROM promotions WHERE store_id=? AND active=1 ORDER BY priority DESC, created_at ASC", (sid,)
    ).fetchall()
    out = []
    for r in rows:
        if r["starts_at"] and r["starts_at"] > now:
            continue
        if r["ends_at"] and r["ends_at"] < now:
            continue
        out.append(r)
    return out


def _lines(payload: dict) -> list[dict]:
    out = []
    for l in payload.get("lines") or []:
        try:
            pid = str(l.get("productId") or "").strip()
            qty = int(l.get("quantity") or 0)
            price = float(l.get("unitPrice") or 0)
        except (TypeError, ValueError):
            continue
        if pid and qty > 0 and price >= 0:
            out.append({"productId": pid, "quantity": qty, "unitPrice": price})
    return out


def _apply_one(promo: sqlite3.Row, lines: list[dict], subtotal: float) -> tuple[float, str] | None:
    """Return (discount, reason) if the promotion applies to this basket, else None.
    The discount is never more than the subtotal; the caller clamps the running total too."""
    try:
        p = json.loads(promo["params"] or "{}")
    except json.JSONDecodeError:
        p = {}
    t = promo["type"]

    if t == "percent_off_cart":
        threshold = float(p.get("threshold") or 0)
        pct = max(0.0, min(float(p.get("percent") or 0), 100.0))
        if subtotal >= threshold and pct > 0:
            return round(subtotal * pct / 100.0, 2), f"{pct:g}% off basket"
        return None

    if t == "fixed_off_cart":
        threshold = float(p.get("threshold") or 0)
        amount = max(0.0, float(p.get("amount") or 0))
        if subtotal >= threshold and amount > 0:
            return round(min(amount, subtotal), 2), f"{amount:g} off basket"
        return None

    if t == "buy_x_get_y":
        pid = str(p.get("productId") or "").strip()
        x = int(p.get("buy") or 0)
        y = int(p.get("free") or 0)
        if not pid or x <= 0 or y <= 0:
            return None
        line = next((l for l in lines if l["productId"] == pid), None)
        if not line:
            return None
        group = x + y
        free_units = (line["quantity"] // group) * y
        if free_units <= 0:
            return None
        return round(free_units * line["unitPrice"], 2), f"buy {x} get {y} on {pid} ({free_units} free)"

    if t == "bundle":
        need = {str(k): int(v) for k, v in (p.get("items") or {}).items()}
        price = max(0.0, float(p.get("bundlePrice") or 0))
        if not need:
            return None
        have = {l["productId"]: l["quantity"] for l in lines}
        sets = min((have.get(pid, 0) // qty) for pid, qty in need.items()) if need else 0
        if sets <= 0:
            return None
        # What those sets would cost at line prices, minus the bundle price.
        unit = {l["productId"]: l["unitPrice"] for l in lines}
        full = sum(unit.get(pid, 0) * qty for pid, qty in need.items())
        saving = (full - price) * sets
        if saving <= 0:
            return None
        return round(saving, 2), f"bundle × {sets}"

    return None


def _evaluate(c: sqlite3.Connection, sid: str, lines: list[dict]) -> dict:
    subtotal = round(sum(l["quantity"] * l["unitPrice"] for l in lines), 2)
    applied: list[dict] = []
    total = 0.0
    for promo in _active(c, sid):
        remaining = subtotal - total
        if remaining <= 0:
            break
        result = _apply_one(promo, lines, subtotal)
        if result is None:
            continue
        discount, reason = result
        discount = min(discount, remaining)
        if discount <= 0:
            continue
        applied.append({"id": promo["id"], "name": promo["name"], "type": promo["type"],
                        "discount": round(discount, 2), "reason": reason})
        total += discount
        if not promo["stackable"]:
            break
    return {"subtotal": subtotal, "discount": round(total, 2),
            "total": round(subtotal - total, 2), "applied": applied}


@app.post("/api/v1/public/evaluate")
async def evaluate(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    lines = _lines(_payload(body))
    with closing(connect()) as c:
        return JSONResponse(_evaluate(c, _store(request), lines))


# ----------------------------------------------------------------- the staff

@app.get("/api/v1/admin/list")
async def list_promos(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT id,name,type,params,priority,stackable,active,starts_at,ends_at FROM promotions "
            "WHERE store_id=? ORDER BY priority DESC, created_at ASC", (_store(request),)).fetchall()]
    for r in rows:
        try:
            r["params"] = json.loads(r["params"] or "{}")
        except json.JSONDecodeError:
            r["params"] = {}
        r["stackable"], r["active"] = bool(r["stackable"]), bool(r["active"])
    return JSONResponse({"promotions": rows})


@app.post("/api/v1/admin/create")
async def create_promo(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    name = str(d.get("name") or "").strip()[:120]
    t = str(d.get("type") or "").strip()
    if not name or t not in TYPES:
        return JSONResponse({"error": f"name and a type in {TYPES} are required"}, status_code=400)
    params = d.get("params") if isinstance(d.get("params"), dict) else {}
    pid = str(uuid.uuid4())
    with closing(connect()) as c:
        c.execute(
            "INSERT INTO promotions(id,store_id,name,type,params,priority,stackable,active,starts_at,ends_at,created_at) "
            "VALUES(?,?,?,?,?,?,?,?,?,?,?)",
            (pid, _store(request), name, t, json.dumps(params), int(d.get("priority") or 0),
             1 if d.get("stackable", True) else 0, 1 if d.get("active", True) else 0,
             d.get("startsAt"), d.get("endsAt"), datetime.now(timezone.utc).isoformat()),
        )
        c.commit()
    return JSONResponse({"created": True, "id": pid})


@app.post("/api/v1/admin/toggle")
async def toggle_promo(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    pid = str(d.get("id") or "").strip()
    active = 1 if d.get("active", True) else 0
    with closing(connect()) as c:
        n = c.execute("UPDATE promotions SET active=? WHERE store_id=? AND id=?",
                      (active, _store(request), pid)).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such promotion"}, status_code=404)
    return JSONResponse({"id": pid, "active": bool(active)})


@app.post("/api/v1/admin/delete")
async def delete_promo(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    pid = str(_payload(body).get("id") or "").strip()
    with closing(connect()) as c:
        n = c.execute("DELETE FROM promotions WHERE store_id=? AND id=?", (_store(request), pid)).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such promotion"}, status_code=404)
    return JSONResponse({"deleted": True, "id": pid})


def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        total = c.execute("SELECT COUNT(*) n FROM promotions WHERE store_id=?", (sid,)).fetchone()["n"]
        active = len(_active(c, sid))
    return {"promotions": int(total), "active": int(active)}


@app.get("/api/v1/admin/summary")
async def admin_summary(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse(_summary(_store(request)))


@app.get("/api/v1/admin/{_rest:path}")
@app.get("/api/v1/admin/")
async def admin_dashboard(request: Request):
    if (e := _verify(request, b"")) is not None:
        return HTMLResponse(f"<p>Unauthorized: {e}</p>", status_code=401)
    sid = _store(request)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT name,type,priority,stackable,active FROM promotions WHERE store_id=? ORDER BY priority DESC, created_at ASC",
            (sid,)).fetchall()]
    labels = {"percent_off_cart": "٪ تخفیف سبد", "fixed_off_cart": "مبلغ ثابت تخفیف",
              "buy_x_get_y": "بخر X بگیر Y", "bundle": "بستهٔ محصولات"}
    body_rows = "".join(
        f"<tr><td>{r['name']}</td><td>{labels.get(r['type'], r['type'])}</td><td>{r['priority']}</td>"
        f"<td>{'بله' if r['stackable'] else 'خیر'}</td>"
        f"<td>{'<span class=on>فعال</span>' if r['active'] else '<span class=off>غیرفعال</span>'}</td></tr>"
        for r in rows
    ) or "<tr><td colspan='5'>هنوز تخفیفی تعریف نشده.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Advanced Promotions</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:24px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:140px}}
.card .n{{font-size:28px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}th{{background:#f4f4f8;font-weight:600}}
.on{{color:#0a7d33;font-weight:600}}.off{{color:#9ca3af}}</style>
</head><body>
<h1>تخفیف‌های پیشرفته</h1>
<p class="sub">فراتر از کوپن تک‌کدی: درصدی/مبلغی روی سبد، «بخر X بگیر Y» و بستهٔ محصولات. همین موتور، هم در صفحهٔ سبد و هم سرِ پرداخت قیمت را حساب می‌کند.</p>
<div class="cards">
  <div class="card"><div class="n">{_summary(sid)['promotions']}</div><div class="l">کل تخفیف‌ها</div></div>
  <div class="card"><div class="n">{_summary(sid)['active']}</div><div class="l">فعال (در بازهٔ زمانی)</div></div>
</div>
<table><tr><th>نام</th><th>نوع</th><th>اولویت</th><th>قابل‌جمع</th><th>وضعیت</th></tr>{body_rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": datetime.now(timezone.utc).isoformat()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Advanced Promotions Service")
