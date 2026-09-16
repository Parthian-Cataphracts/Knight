"""
Customer Segmentation — a real KNIGHT external service.

The store forwards what its customers do — they register, they pay for orders,
they get refunded — and this service keeps one behavioural profile per customer
from that stream: how many orders, how much spent, when they first and last
bought. Segments are then just questions asked of those profiles: who is new,
who buys again, who is a big spender, who has gone quiet. The built-in segments
answer the usual ones; the merchant can save their own rule as a named segment.

No profile is ever invented from a total the caller sends — every number is the
sum of events the store actually forwarded. Partitioned by X-Knight-Store; every
request is the canonical HMAC every external service verifies.
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
from datetime import datetime, timedelta, timezone

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse

VERSION = "2.1.0"
DB_PATH = os.environ.get("CUSTOMER_SEGMENTATION_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("CUSTOMER_SEGMENTATION_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

app = FastAPI(title="KNIGHT Customer Segmentation Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS profiles (store_id TEXT NOT NULL, customer_id TEXT NOT NULL, "
            "orders INTEGER NOT NULL DEFAULT 0, spent REAL NOT NULL DEFAULT 0, "
            "registered_at TEXT, first_order_at TEXT, last_order_at TEXT, updated_at TEXT NOT NULL, "
            "PRIMARY KEY(store_id, customer_id))"
        )
        # Which order amounts we have already counted, so an at-least-once
        # redelivery of order.paid does not inflate spend or order count.
        c.execute(
            "CREATE TABLE IF NOT EXISTS counted_orders (store_id TEXT NOT NULL, order_id TEXT NOT NULL, "
            "customer_id TEXT NOT NULL, amount REAL NOT NULL, PRIMARY KEY(store_id, order_id))"
        )
        c.execute(
            "CREATE TABLE IF NOT EXISTS segments (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, name TEXT NOT NULL, "
            "min_orders INTEGER, min_spent REAL, inactive_days INTEGER, registered_within_days INTEGER, "
            "created_at TEXT NOT NULL)"
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


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


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
            (sid, secret, 1 if d.get("enabled", True) else 0, _now()),
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
            (sid, secret, prev, exp, _now(), prev, exp),
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


# --------------------------------------------------------- profiles (events)

def _touch(c: sqlite3.Connection, sid: str, cid: str) -> None:
    c.execute(
        "INSERT OR IGNORE INTO profiles(store_id,customer_id,updated_at) VALUES(?,?,?)",
        (sid, cid, _now()),
    )


@app.post("/hooks/customer-registered")
async def hook_registered(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    cid = str(d.get("customerId") or d.get("subject") or "").strip()
    if cid:
        at = str(d.get("occurredAt") or _now())
        with closing(connect()) as c:
            _touch(c, _store(request), cid)
            c.execute(
                "UPDATE profiles SET registered_at=COALESCE(registered_at,?), updated_at=? WHERE store_id=? AND customer_id=?",
                (at, _now(), _store(request), cid),
            )
            c.commit()
    return JSONResponse({"tracked": True})


@app.post("/hooks/customer-updated")
async def hook_updated(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    cid = str(_payload(body).get("customerId") or _payload(body).get("subject") or "").strip()
    if cid:
        with closing(connect()) as c:
            _touch(c, _store(request), cid)
            c.commit()
    return JSONResponse({"tracked": True})


@app.post("/hooks/order-paid")
async def hook_paid(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    sid = _store(request)
    cid = str(d.get("customerId") or "").strip()
    oid = str(d.get("orderId") or d.get("id") or "").strip()
    amount = float(d.get("amount") or 0)
    at = str(d.get("occurredAt") or _now())
    if cid and oid:
        with closing(connect()) as c:
            # Idempotent on order id: count each paid order once.
            if c.execute("SELECT 1 FROM counted_orders WHERE store_id=? AND order_id=?", (sid, oid)).fetchone() is None:
                c.execute("INSERT INTO counted_orders(store_id,order_id,customer_id,amount) VALUES(?,?,?,?)",
                          (sid, oid, cid, amount))
                _touch(c, sid, cid)
                c.execute(
                    "UPDATE profiles SET orders=orders+1, spent=spent+?, "
                    "first_order_at=COALESCE(first_order_at,?), last_order_at=?, updated_at=? "
                    "WHERE store_id=? AND customer_id=?",
                    (amount, at, at, _now(), sid, cid),
                )
                c.commit()
    return JSONResponse({"tracked": True})


@app.post("/hooks/order-refunded")
async def hook_refunded(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    sid = _store(request)
    oid = str(d.get("orderId") or "").strip()
    if oid:
        with closing(connect()) as c:
            row = c.execute("SELECT customer_id, amount FROM counted_orders WHERE store_id=? AND order_id=?", (sid, oid)).fetchone()
            if row is not None:
                # Reverse the order we had counted: one fewer order, spend back out.
                c.execute("DELETE FROM counted_orders WHERE store_id=? AND order_id=?", (sid, oid))
                c.execute(
                    "UPDATE profiles SET orders=MAX(0,orders-1), spent=MAX(0,spent-?), updated_at=? "
                    "WHERE store_id=? AND customer_id=?",
                    (float(row["amount"]), _now(), sid, row["customer_id"]),
                )
                c.commit()
    return JSONResponse({"tracked": True})


@app.post("/hooks/{_rest:path}")
async def hook_other(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"tracked": False, "reason": "not a segmentation event"})


# --------------------------------------------------------------- segments

BUILTIN = {
    "new": {"registered_within_days": 30, "min_orders": 0, "max_orders": 0},
    "one_time": {"min_orders": 1, "max_orders": 1},
    "repeat": {"min_orders": 2},
    "vip": {"min_spent": 10_000_000},
    "at_risk": {"min_orders": 1, "inactive_days": 90},
}


def _where(rule: dict) -> tuple[str, list]:
    clauses, args = [], []
    if (mo := rule.get("min_orders")) is not None:
        clauses.append("orders >= ?"); args.append(int(mo))
    if (xo := rule.get("max_orders")) is not None:
        clauses.append("orders <= ?"); args.append(int(xo))
    if (ms := rule.get("min_spent")) is not None:
        clauses.append("spent >= ?"); args.append(float(ms))
    if (rw := rule.get("registered_within_days")) is not None:
        cutoff = (datetime.now(timezone.utc) - timedelta(days=int(rw))).isoformat()
        clauses.append("registered_at IS NOT NULL AND registered_at >= ?"); args.append(cutoff)
    if (inact := rule.get("inactive_days")) is not None:
        cutoff = (datetime.now(timezone.utc) - timedelta(days=int(inact))).isoformat()
        clauses.append("last_order_at IS NOT NULL AND last_order_at < ?"); args.append(cutoff)
    return (" AND ".join(clauses) if clauses else "1=1"), args


def _count(c: sqlite3.Connection, sid: str, rule: dict) -> int:
    where, args = _where(rule)
    return int(c.execute(f"SELECT COUNT(*) n FROM profiles WHERE store_id=? AND {where}", (sid, *args)).fetchone()["n"])


def _custom_rules(c: sqlite3.Connection, sid: str) -> list[dict]:
    rows = c.execute("SELECT * FROM segments WHERE store_id=? ORDER BY created_at ASC", (sid,)).fetchall()
    out = []
    for r in rows:
        rule = {}
        if r["min_orders"] is not None:
            rule["min_orders"] = r["min_orders"]
        if r["min_spent"] is not None:
            rule["min_spent"] = r["min_spent"]
        if r["inactive_days"] is not None:
            rule["inactive_days"] = r["inactive_days"]
        if r["registered_within_days"] is not None:
            rule["registered_within_days"] = r["registered_within_days"]
        out.append({"id": r["id"], "name": r["name"], "rule": rule})
    return out


def _resolve(c: sqlite3.Connection, sid: str, key: str) -> dict | None:
    if key in BUILTIN:
        return BUILTIN[key]
    for cs in _custom_rules(c, sid):
        if cs["id"] == key or cs["name"] == key:
            return cs["rule"]
    return None


# ----------------------------------------------------------------- the staff

@app.get("/api/v1/admin/summary")
async def admin_summary(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    with closing(connect()) as c:
        total = int(c.execute("SELECT COUNT(*) n FROM profiles WHERE store_id=?", (sid,)).fetchone()["n"])
        builtin = {k: _count(c, sid, r) for k, r in BUILTIN.items()}
        custom = [{"id": cs["id"], "name": cs["name"], "count": _count(c, sid, cs["rule"])}
                  for cs in _custom_rules(c, sid)]
    return JSONResponse({"customers": total, "builtin": builtin, "custom": custom})


@app.get("/api/v1/admin/members")
async def members(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    key = request.query_params.get("segment", "").strip()
    with closing(connect()) as c:
        rule = _resolve(c, sid, key)
        if rule is None:
            return JSONResponse({"error": "no such segment"}, status_code=404)
        where, args = _where(rule)
        rows = c.execute(
            f"SELECT customer_id, orders, spent, last_order_at FROM profiles WHERE store_id=? AND {where} "
            "ORDER BY spent DESC LIMIT 500", (sid, *args),
        ).fetchall()
    return JSONResponse({"segment": key, "count": len(rows),
                         "members": [{"customerId": r["customer_id"], "orders": r["orders"],
                                      "spent": r["spent"], "lastOrderAt": r["last_order_at"]} for r in rows]})


@app.post("/api/v1/admin/create")
async def create_segment(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    name = str(d.get("name") or "").strip()[:120]
    if not name:
        return JSONResponse({"error": "name required"}, status_code=400)

    def _opt_int(v):
        return int(v) if v is not None and str(v) != "" else None

    def _opt_float(v):
        return float(v) if v is not None and str(v) != "" else None

    sid_new = str(uuid.uuid4())
    with closing(connect()) as c:
        c.execute(
            "INSERT INTO segments(id,store_id,name,min_orders,min_spent,inactive_days,registered_within_days,created_at) "
            "VALUES(?,?,?,?,?,?,?,?)",
            (sid_new, _store(request), name, _opt_int(d.get("minOrders")), _opt_float(d.get("minSpent")),
             _opt_int(d.get("inactiveDays")), _opt_int(d.get("registeredWithinDays")), _now()),
        )
        c.commit()
    return JSONResponse({"created": True, "id": sid_new})


@app.post("/api/v1/admin/delete")
async def delete_segment(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid_del = str(_payload(body).get("id") or "").strip()
    with closing(connect()) as c:
        n = c.execute("DELETE FROM segments WHERE store_id=? AND id=?", (_store(request), sid_del)).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such segment"}, status_code=404)
    return JSONResponse({"deleted": True, "id": sid_del})


@app.get("/api/v1/admin/{_rest:path}")
@app.get("/api/v1/admin/")
async def admin_dashboard(request: Request):
    if (e := _verify(request, b"")) is not None:
        return HTMLResponse(f"<p>Unauthorized: {e}</p>", status_code=401)
    sid = _store(request)
    with closing(connect()) as c:
        total = int(c.execute("SELECT COUNT(*) n FROM profiles WHERE store_id=?", (sid,)).fetchone()["n"])
        builtin = {k: _count(c, sid, r) for k, r in BUILTIN.items()}
        custom = [{"name": cs["name"], "count": _count(c, sid, cs["rule"])} for cs in _custom_rules(c, sid)]
    labels = {"new": "تازه‌وارد (بی‌خرید)", "one_time": "تک‌خرید", "repeat": "خریدار تکراری",
              "vip": "پرارزش (VIP)", "at_risk": "در خطر ریزش"}
    cards = "".join(
        f"<div class='card'><div class='n'>{builtin[k]}</div><div class='l'>{labels[k]}</div></div>"
        for k in BUILTIN
    )
    custom_rows = "".join(f"<tr><td>{cs['name']}</td><td>{cs['count']}</td></tr>" for cs in custom) \
        or "<tr><td colspan='2'>سگمنت سفارشی‌ای تعریف نشده.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Customer Segmentation</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}h2{{font-size:15px;margin:24px 0 8px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:8px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:130px}}
.card .n{{font-size:26px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}th{{background:#f4f4f8;font-weight:600}}</style>
</head><body>
<h1>بخش‌بندی مشتریان</h1>
<p class="sub">هر مشتری یک پروفایل رفتاری از جریان رویدادها دارد؛ سگمنت‌ها پرسش از همین پروفایل‌ها هستند. کل مشتریان ردیابی‌شده: {total}.</p>
<div class="cards">{cards}</div>
<h2>سگمنت‌های سفارشی</h2>
<table><tr><th>نام</th><th>تعداد</th></tr>{custom_rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": _now()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Customer Segmentation Service")
