"""
Restaurant Operations — a real KNIGHT external service.

A food business runs on two things a plain store does not model: the tables in
the room and the queue in the kitchen. This service is both. The merchant sets up
tables; every paid order the store forwards becomes a kitchen ticket, and each
ticket walks a fixed line — received → preparing → ready → served — that the
staff advance one step at a time and never backwards. A cancelled order pulls its
ticket. The kitchen screen is the live queue; the floor screen is the tables.

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

VERSION = "2.0.0"
DB_PATH = os.environ.get("RESTAURANT_OPERATIONS_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("RESTAURANT_OPERATIONS_CONTROL_SECRET", "")
SKEW_DEFAULT = 300
FLOW = ["Received", "Preparing", "Ready", "Served"]

app = FastAPI(title="KNIGHT Restaurant Operations Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS tables (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, name TEXT NOT NULL, "
            "seats INTEGER NOT NULL DEFAULT 2, active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL)"
        )
        c.execute(
            "CREATE TABLE IF NOT EXISTS tickets (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, order_id TEXT, "
            "table_id TEXT, items TEXT NOT NULL DEFAULT '', amount REAL NOT NULL DEFAULT 0, "
            "status TEXT NOT NULL DEFAULT 'Received', created_at TEXT NOT NULL, updated_at TEXT NOT NULL)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_tk ON tickets(store_id, status)")
        # One ticket per order, so an at-least-once order.paid does not open two.
        c.execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_tk_order ON tickets(store_id, order_id)")
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


# --------------------------------------------------------- kitchen (events)

@app.post("/hooks/order-paid")
async def hook_paid(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    sid = _store(request)
    oid = str(d.get("orderId") or d.get("id") or "").strip()
    amount = float(d.get("amount") or 0)
    if oid:
        with closing(connect()) as c:
            # One ticket per order (unique index absorbs redelivery).
            c.execute(
                "INSERT OR IGNORE INTO tickets(id,store_id,order_id,items,amount,status,created_at,updated_at) "
                "VALUES(?,?,?,?,?, 'Received', ?, ?)",
                (str(uuid.uuid4()), sid, oid, str(d.get("items") or ""), amount, _now(), _now()),
            )
            c.commit()
    return JSONResponse({"ticketed": True})


@app.post("/hooks/order-cancelled")
async def hook_cancelled(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    oid = str(_payload(body).get("orderId") or "").strip()
    if oid:
        with closing(connect()) as c:
            c.execute("UPDATE tickets SET status='Cancelled', updated_at=? WHERE store_id=? AND order_id=? AND status!='Served'",
                      (_now(), _store(request), oid))
            c.commit()
    return JSONResponse({"cancelled": True})


@app.post("/hooks/{_rest:path}")
async def hook_other(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"recorded": False, "reason": "not a kitchen event"})


# ----------------------------------------------------------------- tables

@app.get("/api/v1/admin/tables")
async def list_tables(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT id,name,seats,active FROM tables WHERE store_id=? ORDER BY created_at ASC", (_store(request),)).fetchall()]
    for r in rows:
        r["active"] = bool(r["active"])
    return JSONResponse({"tables": rows})


@app.post("/api/v1/admin/table-create")
async def create_table(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    name = str(d.get("name") or "").strip()[:60]
    if not name:
        return JSONResponse({"error": "name required"}, status_code=400)
    tid = str(uuid.uuid4())
    with closing(connect()) as c:
        c.execute("INSERT INTO tables(id,store_id,name,seats,active,created_at) VALUES(?,?,?,?,?,?)",
                  (tid, _store(request), name, max(1, int(d.get("seats") or 2)), 1 if d.get("active", True) else 0, _now()))
        c.commit()
    return JSONResponse({"created": True, "id": tid})


# ----------------------------------------------------------------- tickets

@app.post("/api/v1/admin/ticket-create")
async def create_ticket(request: Request):
    """A walk-in / phone order the kitchen needs without a store order behind it."""
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    tid = str(uuid.uuid4())
    with closing(connect()) as c:
        c.execute(
            "INSERT INTO tickets(id,store_id,order_id,table_id,items,amount,status,created_at,updated_at) "
            "VALUES(?,?,?,?,?,?, 'Received', ?, ?)",
            (tid, _store(request), None, str(d.get("tableId") or "") or None, str(d.get("items") or "")[:2000],
             float(d.get("amount") or 0), _now(), _now()),
        )
        c.commit()
    return JSONResponse({"created": True, "id": tid, "status": "Received"})


@app.post("/api/v1/admin/advance")
async def advance(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    tid = str(d.get("id") or "").strip()
    with closing(connect()) as c:
        row = c.execute("SELECT status FROM tickets WHERE store_id=? AND id=?", (sid, tid)).fetchone()
        if row is None:
            return JSONResponse({"error": "no such ticket"}, status_code=404)
        cur = row["status"]
        if cur == "Cancelled":
            return JSONResponse({"error": "ticket is cancelled"}, status_code=409)
        if cur == "Served":
            return JSONResponse({"error": "already served"}, status_code=409)
        nxt = FLOW[FLOW.index(cur) + 1]
        c.execute("UPDATE tickets SET status=?, updated_at=? WHERE store_id=? AND id=?", (nxt, _now(), sid, tid))
        c.commit()
    return JSONResponse({"id": tid, "status": nxt})


@app.post("/api/v1/admin/assign")
async def assign_table(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    tid = str(d.get("id") or "").strip()
    table = str(d.get("tableId") or "").strip() or None
    with closing(connect()) as c:
        n = c.execute("UPDATE tickets SET table_id=?, updated_at=? WHERE store_id=? AND id=?",
                      (table, _now(), _store(request), tid)).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such ticket"}, status_code=404)
    return JSONResponse({"id": tid, "tableId": table})


@app.get("/api/v1/admin/kitchen")
async def kitchen(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT t.id, t.order_id, t.status, t.amount, t.items, t.created_at, tb.name AS table_name "
            "FROM tickets t LEFT JOIN tables tb ON tb.id=t.table_id "
            "WHERE t.store_id=? AND t.status IN ('Received','Preparing','Ready') ORDER BY t.created_at ASC",
            (_store(request),)).fetchall()]
    return JSONResponse({"queue": rows})


def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        tables = int(c.execute("SELECT COUNT(*) n FROM tables WHERE store_id=? AND active=1", (sid,)).fetchone()["n"])
        by = {r["status"]: int(r["n"]) for r in c.execute(
            "SELECT status, COUNT(*) n FROM tickets WHERE store_id=? GROUP BY status", (sid,)).fetchall()}
    return {"tables": tables, "received": by.get("Received", 0), "preparing": by.get("Preparing", 0),
            "ready": by.get("Ready", 0), "served": by.get("Served", 0), "cancelled": by.get("Cancelled", 0)}


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
        q = [dict(r) for r in c.execute(
            "SELECT t.order_id, t.status, t.amount, tb.name AS table_name FROM tickets t "
            "LEFT JOIN tables tb ON tb.id=t.table_id WHERE t.store_id=? AND t.status IN ('Received','Preparing','Ready') "
            "ORDER BY t.created_at ASC", (sid,)).fetchall()]
    st_fa = {"Received": "دریافت‌شده", "Preparing": "در حال آماده‌سازی", "Ready": "آماده"}
    rows = "".join(
        f"<tr><td dir='ltr'>{(x['order_id'] or '—')[:12]}</td><td>{x['table_name'] or '—'}</td>"
        f"<td dir='ltr'>{x['amount']:,.0f}</td><td>{st_fa.get(x['status'], x['status'])}</td></tr>"
        for x in q
    ) or "<tr><td colspan='4'>صف آشپزخانه خالی است.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Restaurant Operations</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}h2{{font-size:15px;margin:24px 0 8px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:8px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:110px}}
.card .n{{font-size:24px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}th{{background:#f4f4f8;font-weight:600}}</style>
</head><body>
<h1>عملیات رستوران</h1>
<p class="sub">هر سفارش پرداخت‌شده یک تیکت آشپزخانه می‌شود و مسیر دریافت → آماده‌سازی → آماده → سرو را طی می‌کند.</p>
<div class="cards">
  <div class="card"><div class="n">{s['tables']}</div><div class="l">میز فعال</div></div>
  <div class="card"><div class="n">{s['received']}</div><div class="l">دریافت‌شده</div></div>
  <div class="card"><div class="n">{s['preparing']}</div><div class="l">در حال آماده‌سازی</div></div>
  <div class="card"><div class="n">{s['ready']}</div><div class="l">آماده</div></div>
  <div class="card"><div class="n">{s['served']}</div><div class="l">سروشده</div></div>
</div>
<h2>صف آشپزخانه</h2>
<table><tr><th>سفارش</th><th>میز</th><th>مبلغ</th><th>وضعیت</th></tr>{rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": _now()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Restaurant Operations Service")
