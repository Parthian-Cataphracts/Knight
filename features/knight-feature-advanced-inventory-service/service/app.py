"""
Advanced Inventory — a real KNIGHT external service.

The store keeps one number for a product's stock; this service lets the merchant
split that stock across real places — a shop floor, a back room, a second branch
— and warns before any of them runs out.

Stock is an append-only ledger: on-hand at a location is the sum of its
movements, never a column that can drift. The store's own stock is the truth for
the "main" location: every product.stock_changed it forwards is reconciled into
one movement so main always equals what the store believes it has. Everything
else — extra locations, transfers between them, and reorder rules — is the
merchant's to manage from the screen, and is proxied here as staff.

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
DB_PATH = os.environ.get("ADVANCED_INVENTORY_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("ADVANCED_INVENTORY_CONTROL_SECRET", "")
SKEW_DEFAULT = 300
MAIN = "main"

app = FastAPI(title="KNIGHT Advanced Inventory Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS products (store_id TEXT NOT NULL, product_id TEXT NOT NULL, "
            "title TEXT NOT NULL DEFAULT '', updated_at TEXT NOT NULL, PRIMARY KEY(store_id, product_id))"
        )
        c.execute(
            "CREATE TABLE IF NOT EXISTS locations (store_id TEXT NOT NULL, code TEXT NOT NULL, "
            "name TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(store_id, code))"
        )
        # The ledger. On-hand is SUM(delta); rows are never updated or deleted.
        c.execute(
            "CREATE TABLE IF NOT EXISTS movements (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, "
            "product_id TEXT NOT NULL, location_code TEXT NOT NULL, delta INTEGER NOT NULL, "
            "reason TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_mov ON movements(store_id, product_id, location_code)")
        c.execute(
            "CREATE TABLE IF NOT EXISTS rules (store_id TEXT NOT NULL, product_id TEXT NOT NULL, "
            "location_code TEXT NOT NULL, reorder_point INTEGER NOT NULL DEFAULT 0, "
            "reorder_qty INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(store_id, product_id, location_code))"
        )
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


# --------------------------------------------------------------- ledger core

def _ensure_location(c: sqlite3.Connection, sid: str, code: str, name: str | None = None) -> None:
    c.execute(
        "INSERT OR IGNORE INTO locations(store_id,code,name,created_at) VALUES(?,?,?,?)",
        (sid, code, name or code, datetime.now(timezone.utc).isoformat()),
    )


def _onhand(c: sqlite3.Connection, sid: str, product_id: str, code: str) -> int:
    row = c.execute(
        "SELECT COALESCE(SUM(delta),0) q FROM movements WHERE store_id=? AND product_id=? AND location_code=?",
        (sid, product_id, code),
    ).fetchone()
    return int(row["q"])


def _move(c: sqlite3.Connection, sid: str, product_id: str, code: str, delta: int, reason: str) -> None:
    c.execute(
        "INSERT INTO movements(id,store_id,product_id,location_code,delta,reason,created_at) VALUES(?,?,?,?,?,?,?)",
        (str(uuid.uuid4()), sid, product_id, code, int(delta), reason, datetime.now(timezone.utc).isoformat()),
    )


# --------------------------------------------------------- indexing (events)

@app.post("/hooks/product-created")
@app.post("/hooks/product-updated")
async def hook_product(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    pid = str(d.get("productId") or d.get("subject") or "").strip()
    if pid:
        with closing(connect()) as c:
            c.execute(
                "INSERT INTO products(store_id,product_id,title,updated_at) VALUES(?,?,?,?) "
                "ON CONFLICT(store_id,product_id) DO UPDATE SET title=excluded.title, updated_at=excluded.updated_at",
                (_store(request), pid, str(d.get("title") or ""), datetime.now(timezone.utc).isoformat()),
            )
            c.commit()
    return JSONResponse({"tracked": True})


@app.post("/hooks/product-stock-changed")
async def hook_stock(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    sid = _store(request)
    pid = str(d.get("productId") or d.get("subject") or "").strip()
    if pid:
        target = int(d.get("stock") or 0)
        with closing(connect()) as c:
            _ensure_location(c, sid, MAIN, "انبار اصلی")
            # Reconcile main to the store's authoritative number with one
            # balancing movement, so the ledger stays append-only and main
            # always equals what the store believes it has.
            current = _onhand(c, sid, pid, MAIN)
            if target != current:
                _move(c, sid, pid, MAIN, target - current, "sync: store stock")
            c.commit()
    return JSONResponse({"tracked": True})


@app.post("/hooks/{_rest:path}")
async def hook_other(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"tracked": False, "reason": "not an inventory event"})


# ----------------------------------------------------------------- the staff

@app.get("/api/v1/admin/locations")
async def list_locations(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    with closing(connect()) as c:
        _ensure_location(c, sid, MAIN, "انبار اصلی")
        c.commit()
        rows = [dict(r) for r in c.execute(
            "SELECT code, name, created_at FROM locations WHERE store_id=? ORDER BY code", (sid,)).fetchall()]
    return JSONResponse({"locations": rows})


@app.post("/api/v1/admin/locations")
async def create_location(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    code = str(d.get("code") or "").strip().lower().replace(" ", "-")[:40]
    name = str(d.get("name") or "").strip()[:120] or code
    if not code:
        return JSONResponse({"error": "code required"}, status_code=400)
    with closing(connect()) as c:
        _ensure_location(c, _store(request), code, name)
        c.commit()
    return JSONResponse({"created": True, "code": code, "name": name})


@app.get("/api/v1/admin/stock")
async def product_stock(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    pid = request.query_params.get("productId", "").strip()
    if not pid:
        return JSONResponse({"error": "productId required"}, status_code=400)
    with closing(connect()) as c:
        rows = c.execute(
            "SELECT location_code, COALESCE(SUM(delta),0) q FROM movements "
            "WHERE store_id=? AND product_id=? GROUP BY location_code ORDER BY location_code",
            (sid, pid),
        ).fetchall()
        title = c.execute("SELECT title FROM products WHERE store_id=? AND product_id=?", (sid, pid)).fetchone()
    by_loc = [{"location": r["location_code"], "onHand": int(r["q"])} for r in rows]
    return JSONResponse({
        "productId": pid, "title": title["title"] if title else "",
        "total": sum(x["onHand"] for x in by_loc), "byLocation": by_loc,
    })


@app.post("/api/v1/admin/adjust")
async def adjust(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    pid = str(d.get("productId") or "").strip()
    code = str(d.get("locationCode") or MAIN).strip().lower()
    try:
        delta = int(d.get("delta"))
    except (TypeError, ValueError):
        return JSONResponse({"error": "delta must be an integer"}, status_code=400)
    if not pid or delta == 0:
        return JSONResponse({"error": "productId and a non-zero delta are required"}, status_code=400)
    with closing(connect()) as c:
        _ensure_location(c, sid, code)
        if delta < 0 and _onhand(c, sid, pid, code) + delta < 0:
            return JSONResponse({"error": "would drive on-hand below zero"}, status_code=409)
        _move(c, sid, pid, code, delta, str(d.get("reason") or "manual adjustment")[:200])
        c.commit()
        oh = _onhand(c, sid, pid, code)
    return JSONResponse({"productId": pid, "location": code, "onHand": oh})


@app.post("/api/v1/admin/transfer")
async def transfer(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    pid = str(d.get("productId") or "").strip()
    src = str(d.get("from") or "").strip().lower()
    dst = str(d.get("to") or "").strip().lower()
    try:
        qty = int(d.get("quantity"))
    except (TypeError, ValueError):
        return JSONResponse({"error": "quantity must be an integer"}, status_code=400)
    if not pid or not src or not dst or src == dst or qty <= 0:
        return JSONResponse({"error": "productId, distinct from/to and a positive quantity are required"}, status_code=400)
    with closing(connect()) as c:
        _ensure_location(c, sid, src)
        _ensure_location(c, sid, dst)
        if _onhand(c, sid, pid, src) < qty:
            return JSONResponse({"error": "not enough on hand at the source"}, status_code=409)
        _move(c, sid, pid, src, -qty, f"transfer to {dst}")
        _move(c, sid, pid, dst, qty, f"transfer from {src}")
        c.commit()
        return JSONResponse({"productId": pid, "from": {"location": src, "onHand": _onhand(c, sid, pid, src)},
                             "to": {"location": dst, "onHand": _onhand(c, sid, pid, dst)}})


@app.post("/api/v1/admin/rule")
async def set_rule(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    pid = str(d.get("productId") or "").strip()
    code = str(d.get("locationCode") or MAIN).strip().lower()
    point = max(0, int(d.get("reorderPoint") or 0))
    qty = max(0, int(d.get("reorderQty") or 0))
    if not pid:
        return JSONResponse({"error": "productId required"}, status_code=400)
    with closing(connect()) as c:
        _ensure_location(c, sid, code)
        c.execute(
            "INSERT INTO rules(store_id,product_id,location_code,reorder_point,reorder_qty) VALUES(?,?,?,?,?) "
            "ON CONFLICT(store_id,product_id,location_code) DO UPDATE SET reorder_point=excluded.reorder_point, reorder_qty=excluded.reorder_qty",
            (sid, pid, code, point, qty),
        )
        c.commit()
    return JSONResponse({"productId": pid, "location": code, "reorderPoint": point, "reorderQty": qty})


def _alerts(c: sqlite3.Connection, sid: str) -> list[dict]:
    rows = c.execute(
        "SELECT r.product_id, r.location_code, r.reorder_point, r.reorder_qty, "
        "COALESCE((SELECT SUM(m.delta) FROM movements m WHERE m.store_id=r.store_id AND m.product_id=r.product_id AND m.location_code=r.location_code),0) onhand, "
        "COALESCE((SELECT p.title FROM products p WHERE p.store_id=r.store_id AND p.product_id=r.product_id),'') title "
        "FROM rules r WHERE r.store_id=? ",
        (sid,),
    ).fetchall()
    return [
        {"productId": r["product_id"], "title": r["title"], "location": r["location_code"],
         "onHand": int(r["onhand"]), "reorderPoint": int(r["reorder_point"]), "reorderQty": int(r["reorder_qty"])}
        for r in rows if int(r["onhand"]) <= int(r["reorder_point"])
    ]


@app.get("/api/v1/admin/alerts")
async def alerts(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        return JSONResponse({"alerts": _alerts(c, _store(request))})


def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        _ensure_location(c, sid, MAIN, "انبار اصلی")
        c.commit()
        locs = c.execute("SELECT COUNT(*) n FROM locations WHERE store_id=?", (sid,)).fetchone()["n"]
        prods = c.execute("SELECT COUNT(*) n FROM products WHERE store_id=?", (sid,)).fetchone()["n"]
        total = c.execute("SELECT COALESCE(SUM(delta),0) n FROM movements WHERE store_id=?", (sid,)).fetchone()["n"]
        low = len(_alerts(c, sid))
    return {"locations": int(locs), "products": int(prods), "onHandTotal": int(total), "lowStock": int(low)}


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
    s = _summary(sid)
    with closing(connect()) as c:
        al = _alerts(c, sid)
        locs = [dict(r) for r in c.execute(
            "SELECT code, name FROM locations WHERE store_id=? ORDER BY code", (sid,)).fetchall()]
    loc_rows = "".join(f"<tr><td dir='ltr'>{l['code']}</td><td>{l['name']}</td></tr>" for l in locs) \
        or "<tr><td colspan='2'>موقعیتی نیست.</td></tr>"
    alert_rows = "".join(
        f"<tr><td>{a['title'] or a['productId']}</td><td dir='ltr'>{a['location']}</td>"
        f"<td>{a['onHand']}</td><td>{a['reorderPoint']}</td><td>{a['reorderQty']}</td></tr>"
        for a in al
    ) or "<tr><td colspan='5'>هیچ محصولی به نقطهٔ سفارش مجدد نرسیده.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Advanced Inventory</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}h2{{font-size:15px;margin:24px 0 8px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:8px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:130px}}
.card .n{{font-size:28px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
.card.warn .n{{color:#c1121f}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}th{{background:#f4f4f8;font-weight:600}}</style>
</head><body>
<h1>انبارداری پیشرفته</h1>
<p class="sub">موجودی فروشگاه را میان چند موقعیت تقسیم کنید؛ موجودی هر موقعیت جمع حرکات آن است و «انبار اصلی» همیشه با موجودی فروشگاه هم‌گام می‌ماند.</p>
<div class="cards">
  <div class="card"><div class="n">{s['locations']}</div><div class="l">موقعیت‌ها</div></div>
  <div class="card"><div class="n">{s['products']}</div><div class="l">محصول ردیابی‌شده</div></div>
  <div class="card"><div class="n">{s['onHandTotal']}</div><div class="l">مجموع موجودی</div></div>
  <div class="card warn"><div class="n">{s['lowStock']}</div><div class="l">کم‌موجودی</div></div>
</div>
<h2>هشدار سفارش مجدد</h2>
<table><tr><th>محصول</th><th>موقعیت</th><th>موجودی</th><th>نقطهٔ سفارش</th><th>مقدار سفارش</th></tr>{alert_rows}</table>
<h2>موقعیت‌ها</h2>
<table><tr><th style="text-align:left">کد</th><th>نام</th></tr>{loc_rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": datetime.now(timezone.utc).isoformat()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Advanced Inventory Service")
