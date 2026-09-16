"""
Loyalty and Rewards — a real KNIGHT external service.

Not a counter: it runs the loyalty programme. Points are earned on a paid order
(amount x earn rate), held in **lots** that expire, spent **oldest-first** so the
soonest-to-expire points go first, and a member's lifetime earning decides their
**tier**. A refund claws back the points that order awarded. The merchant gets a
real screen — programme totals, tiers, top members and a per-customer lookup —
and staff can redeem a member's points.

Security is the platform contract, identical to every external service: KNIGHT's
control calls (/knight/stores/register|rotate|revoke) are HMAC-signed with the
per-Feature control secret; each store's webhooks and proxied requests are signed
with the per-store secret KNIGHT delivered. Canonical string:
`METHOD \n path \n timestamp \n nonce \n sha256hex(body)`, header
`X-Knight-Signature: sha256=<hex>`. Data is partitioned by `X-Knight-Store`.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import math
import os
import sqlite3
import time
from contextlib import closing
from datetime import datetime, timedelta, timezone

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse

VERSION = "2.0.0"
DB_PATH = os.environ.get("LOYALTY_REWARDS_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("LOYALTY_REWARDS_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

# Sensible defaults; a store overrides them via delivered configuration later.
DEFAULTS = {
    "earn_rate": 1.0,          # points per 1 currency unit on a paid order
    "point_value": 1000.0,     # currency units one point is worth on redemption
    "lot_expiry_days": 365,    # points expire a year after they are earned
    # lifetime-points thresholds, ascending; the highest reached wins
    "tiers": [
        {"name": "Bronze", "threshold": 0},
        {"name": "Silver", "threshold": 5000},
        {"name": "Gold", "threshold": 20000},
        {"name": "Platinum", "threshold": 50000},
    ],
}

EARN_EVENTS = {"/hooks/order-paid": "order.paid"}
CLAWBACK_EVENTS = {"/hooks/order-refunded": "order.refunded", "/hooks/order-cancelled": "order.cancelled"}

app = FastAPI(title="KNIGHT Loyalty and Rewards Service", version=VERSION)


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
        # One lot per earning event: the points it granted, how many remain, when
        # it was earned and when it expires. Idempotent on (store, event id).
        c.execute(
            "CREATE TABLE IF NOT EXISTS lots (id INTEGER PRIMARY KEY AUTOINCREMENT, store_id TEXT NOT NULL, "
            "customer_id TEXT NOT NULL, order_id TEXT, event_id TEXT, points INTEGER NOT NULL, "
            "remaining INTEGER NOT NULL, earned_at TEXT NOT NULL, expires_at TEXT NOT NULL)"
        )
        c.execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_lots_event ON lots(store_id, event_id) WHERE event_id IS NOT NULL")
        c.execute("CREATE INDEX IF NOT EXISTS ix_lots_cust ON lots(store_id, customer_id)")
        # An append-only ledger of everything that moved points, for the history view.
        c.execute(
            "CREATE TABLE IF NOT EXISTS ledger (id INTEGER PRIMARY KEY AUTOINCREMENT, store_id TEXT NOT NULL, "
            "customer_id TEXT NOT NULL, kind TEXT NOT NULL, points INTEGER NOT NULL, detail TEXT, at TEXT NOT NULL)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_ledger_cust ON ledger(store_id, customer_id)")
        c.execute("CREATE TABLE IF NOT EXISTS config (store_id TEXT PRIMARY KEY, json TEXT NOT NULL)")
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


def _control(request: Request, body: bytes):
    return _check(request, body, [CONTROL_SECRET])


@app.post("/knight/stores/register")
async def register(request: Request):
    body = await request.body()
    if (e := _control(request, body)) is not None:
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
    if (e := _control(request, body)) is not None:
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
    if (e := _control(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = str(json.loads(body or b"{}").get("storeId") or "").strip()
    with closing(connect()) as c:
        c.execute("DELETE FROM store_secrets WHERE store_id=?", (sid,))
        c.commit()
    return JSONResponse({"revoked": True})


# ------------------------------------------------------------- loyalty engine

def get_config(sid: str) -> dict:
    with closing(connect()) as c:
        row = c.execute("SELECT json FROM config WHERE store_id=?", (sid,)).fetchone()
    if row:
        try:
            merged = dict(DEFAULTS)
            merged.update(json.loads(row["json"]))
            return merged
        except json.JSONDecodeError:
            pass
    return dict(DEFAULTS)


def tier_for(config: dict, lifetime: int) -> str:
    name = config["tiers"][0]["name"]
    for t in sorted(config["tiers"], key=lambda x: x["threshold"]):
        if lifetime >= t["threshold"]:
            name = t["name"]
    return name


def balance_of(c: sqlite3.Connection, sid: str, customer: str) -> int:
    now = datetime.now(timezone.utc).isoformat()
    row = c.execute(
        "SELECT COALESCE(SUM(remaining),0) n FROM lots WHERE store_id=? AND customer_id=? AND expires_at>?",
        (sid, customer, now),
    ).fetchone()
    return int(row["n"])


def lifetime_of(c: sqlite3.Connection, sid: str, customer: str) -> int:
    row = c.execute(
        "SELECT COALESCE(SUM(points),0) n FROM ledger WHERE store_id=? AND customer_id=? AND kind='earn'",
        (sid, customer),
    ).fetchone()
    return int(row["n"])


def _payload(body: bytes) -> dict:
    try:
        d = json.loads(body) if body else {}
        return d if isinstance(d, dict) else {}
    except json.JSONDecodeError:
        return {}


@app.post("/hooks/{_rest:path}")
async def hook(request: Request):
    body = await request.body()
    sid = _store(request)
    if not sid:
        return JSONResponse({"error": "no store id"}, status_code=400)
    if (e := _check(request, body, _candidates(sid))) is not None:
        return JSONResponse({"error": e}, status_code=401)

    path = request.url.path
    p = _payload(body)
    customer = str(p.get("customerId") or p.get("subject") or "").strip()
    event_id = str(p.get("id") or p.get("eventId") or "") or None
    order_id = str(p.get("orderId") or p.get("order_id") or "") or None
    config = get_config(sid)
    now = datetime.now(timezone.utc)

    if path in EARN_EVENTS:
        if not customer:
            return JSONResponse({"recorded": False, "reason": "no customer on event"})
        amount = float(p.get("amount") or p.get("total") or p.get("subtotal") or 0)
        points = int(math.floor(amount * float(config["earn_rate"])))
        if points <= 0:
            return JSONResponse({"recorded": False, "reason": "no points for amount", "amount": amount})
        expires = (now + timedelta(days=int(config["lot_expiry_days"]))).isoformat()
        with closing(connect()) as c:
            try:
                c.execute(
                    "INSERT INTO lots(store_id,customer_id,order_id,event_id,points,remaining,earned_at,expires_at) "
                    "VALUES(?,?,?,?,?,?,?,?)",
                    (sid, customer, order_id, event_id, points, points, now.isoformat(), expires),
                )
                c.execute(
                    "INSERT INTO ledger(store_id,customer_id,kind,points,detail,at) VALUES(?,?,?,?,?,?)",
                    (sid, customer, "earn", points, f"order {order_id or '-'} · {amount:g}", now.isoformat()),
                )
                c.commit()
                awarded = True
            except sqlite3.IntegrityError:
                awarded = False  # duplicate delivery
        return JSONResponse({"recorded": awarded, "customer": customer, "points": points})

    if path in CLAWBACK_EVENTS:
        # Claw back the points that this order awarded, if it still has them.
        with closing(connect()) as c:
            lot = c.execute(
                "SELECT id, remaining, points FROM lots WHERE store_id=? AND order_id=? ORDER BY id LIMIT 1",
                (sid, order_id),
            ).fetchone() if order_id else None
            if lot and int(lot["remaining"]) > 0:
                c.execute("UPDATE lots SET remaining=0 WHERE id=?", (lot["id"],))
                c.execute(
                    "INSERT INTO ledger(store_id,customer_id,kind,points,detail,at) VALUES(?,?,?,?,?,?)",
                    (sid, customer or "-", "clawback", -int(lot["remaining"]), f"order {order_id}", now.isoformat()),
                )
                c.commit()
                return JSONResponse({"recorded": True, "clawedBack": int(lot["remaining"])})
        return JSONResponse({"recorded": True, "clawedBack": 0})

    return JSONResponse({"recorded": False, "reason": "event not used by loyalty"})


# --------------------------------------------------------------- staff surface

def _staff_ok(request: Request, body: bytes = b"") -> str | None:
    return _check(request, body, _candidates(_store(request)))


@app.post("/api/v1/admin/redeem")
async def redeem(request: Request):
    body = await request.body()
    if (e := _staff_ok(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    customer = str(d.get("customerId") or "").strip()
    points = int(d.get("points") or 0)
    if not customer or points <= 0:
        return JSONResponse({"error": "customerId and positive points required"}, status_code=400)
    now = datetime.now(timezone.utc)
    with closing(connect()) as c:
        available = balance_of(c, sid, customer)
        if points > available:
            return JSONResponse({"error": "insufficient points", "balance": available}, status_code=409)
        # Spend oldest-expiring first.
        remaining = points
        for lot in c.execute(
            "SELECT id, remaining FROM lots WHERE store_id=? AND customer_id=? AND remaining>0 AND expires_at>? "
            "ORDER BY expires_at ASC",
            (sid, customer, now.isoformat()),
        ).fetchall():
            if remaining <= 0:
                break
            take = min(remaining, int(lot["remaining"]))
            c.execute("UPDATE lots SET remaining=remaining-? WHERE id=?", (take, lot["id"]))
            remaining -= take
        c.execute(
            "INSERT INTO ledger(store_id,customer_id,kind,points,detail,at) VALUES(?,?,?,?,?,?)",
            (sid, customer, "redeem", -points, d.get("detail") or "redeemed", now.isoformat()),
        )
        c.commit()
        config = get_config(sid)
        value = points * float(config["point_value"])
        return JSONResponse({"redeemed": points, "value": value, "balance": balance_of(c, sid, customer)})


@app.get("/api/v1/admin/customer")
async def customer_lookup(request: Request):
    if (e := _staff_ok(request)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    customer = request.query_params.get("id", "").strip()
    if not customer:
        return JSONResponse({"error": "id required"}, status_code=400)
    config = get_config(sid)
    with closing(connect()) as c:
        bal = balance_of(c, sid, customer)
        life = lifetime_of(c, sid, customer)
        history = [
            {"kind": r["kind"], "points": r["points"], "detail": r["detail"], "at": r["at"]}
            for r in c.execute(
                "SELECT kind,points,detail,at FROM ledger WHERE store_id=? AND customer_id=? ORDER BY id DESC LIMIT 50",
                (sid, customer),
            ).fetchall()
        ]
    return JSONResponse({"customer": customer, "balance": bal, "lifetime": life, "tier": tier_for(config, life), "history": history})


def _summary(sid: str) -> dict:
    config = get_config(sid)
    now = datetime.now(timezone.utc).isoformat()
    with closing(connect()) as c:
        members = int(c.execute("SELECT COUNT(DISTINCT customer_id) n FROM lots WHERE store_id=?", (sid,)).fetchone()["n"])
        outstanding = int(c.execute(
            "SELECT COALESCE(SUM(remaining),0) n FROM lots WHERE store_id=? AND expires_at>?", (sid, now)
        ).fetchone()["n"])
        rows = c.execute(
            "SELECT customer_id, COALESCE(SUM(CASE WHEN expires_at>? THEN remaining ELSE 0 END),0) bal "
            "FROM lots WHERE store_id=? GROUP BY customer_id", (now, sid)
        ).fetchall()
        lifetimes = {
            r["customer_id"]: int(r["n"]) for r in c.execute(
                "SELECT customer_id, SUM(points) n FROM ledger WHERE store_id=? AND kind='earn' GROUP BY customer_id", (sid,)
            ).fetchall()
        }
    tiers: dict[str, int] = {}
    top = []
    for r in rows:
        life = lifetimes.get(r["customer_id"], 0)
        t = tier_for(config, life)
        tiers[t] = tiers.get(t, 0) + 1
        top.append({"customer": r["customer_id"], "balance": int(r["bal"]), "lifetime": life, "tier": t})
    top.sort(key=lambda x: x["balance"], reverse=True)
    return {"members": members, "outstanding": outstanding, "tiers": tiers, "top": top[:20], "config": config}


@app.get("/api/v1/admin/summary")
async def admin_summary(request: Request):
    if (e := _staff_ok(request)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse(_summary(_store(request)))


@app.get("/api/v1/admin/{_rest:path}")
@app.get("/api/v1/admin/")
async def admin_dashboard(request: Request):
    if (e := _staff_ok(request)) is not None:
        return HTMLResponse(f"<p>Unauthorized: {e}</p>", status_code=401)
    d = _summary(_store(request))
    cfg = d["config"]
    tier_rows = "".join(
        f"<tr><td>{name}</td><td style='text-align:left'>{count}</td></tr>" for name, count in d["tiers"].items()
    ) or "<tr><td colspan='2'>—</td></tr>"
    top_rows = "".join(
        f"<tr><td dir='ltr'>{m['customer']}</td><td>{m['tier']}</td><td style='text-align:left'>{m['balance']}</td>"
        f"<td style='text-align:left'>{m['lifetime']}</td></tr>"
        for m in d["top"]
    ) or "<tr><td colspan='4'>هنوز عضوی امتیاز نگرفته است.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Loyalty and Rewards</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:24px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:160px}}
.card .n{{font-size:32px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
.grid{{display:grid;grid-template-columns:1fr 2fr;gap:24px}}@media(max-width:760px){{.grid{{grid-template-columns:1fr}}}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:10px 14px;border-bottom:1px solid #eef0f4;font-size:13px}}th{{text-align:right;background:#f4f4f8;font-weight:600}}
caption{{text-align:right;font-weight:600;padding:8px 0}}.cfg{{color:#6b7280;font-size:12px;margin-top:16px}}</style>
</head><body>
<h1>باشگاه مشتریان (Loyalty)</h1>
<p class="sub">امتیازها روی سفارش پرداخت‌شده کسب می‌شوند، در دسته‌هایی با انقضا نگهداری و قدیمی‌ترین‌ها اول خرج می‌شوند.</p>
<div class="cards">
  <div class="card"><div class="n">{d['members']}</div><div class="l">اعضا</div></div>
  <div class="card"><div class="n">{d['outstanding']}</div><div class="l">امتیاز فعال</div></div>
</div>
<div class="grid">
  <table><caption>توزیع سطوح</caption><tr><th>سطح</th><th style="text-align:left">اعضا</th></tr>{tier_rows}</table>
  <table><caption>برترین اعضا</caption><tr><th>مشتری</th><th>سطح</th><th style="text-align:left">موجودی</th><th style="text-align:left">مادام‌العمر</th></tr>{top_rows}</table>
</div>
<p class="cfg">نرخ کسب: {cfg['earn_rate']} امتیاز به‌ازای هر واحد پول · انقضای امتیاز: {cfg['lot_expiry_days']} روز · ارزش هر امتیاز: {cfg['point_value']:g} · سطوح: {', '.join(t['name'] for t in cfg['tiers'])}</p>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": datetime.now(timezone.utc).isoformat()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Loyalty and Rewards Service")
