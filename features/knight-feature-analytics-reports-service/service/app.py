"""
Analytics Reports — a real KNIGHT external service.

Where analytics-core shows a live dashboard, this is the reporting surface: a
day-by-day ledger of what the store took, built from the events it forwards, that
the merchant can query over a date range and export as CSV. Every paid order adds
to that day's revenue and count (idempotently, so an at-least-once redelivery does
not double a day's takings), every refund subtracts, and every registration adds
a new customer.

No total is trusted from the caller — each day's figures are the sum of the
events the store actually sent. Partitioned by X-Knight-Store; every request is
the canonical HMAC every external service verifies.
"""

from __future__ import annotations

import hashlib
import hmac
import io
import json
import os
import sqlite3
import time
from contextlib import closing
from datetime import datetime, timedelta, timezone

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse

VERSION = "2.1.0"
DB_PATH = os.environ.get("ANALYTICS_REPORTS_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("ANALYTICS_REPORTS_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

app = FastAPI(title="KNIGHT Analytics Reports Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS daily (store_id TEXT NOT NULL, day TEXT NOT NULL, "
            "orders INTEGER NOT NULL DEFAULT 0, revenue REAL NOT NULL DEFAULT 0, "
            "refunds REAL NOT NULL DEFAULT 0, new_customers INTEGER NOT NULL DEFAULT 0, "
            "PRIMARY KEY(store_id, day))"
        )
        c.execute(
            "CREATE TABLE IF NOT EXISTS counted_orders (store_id TEXT NOT NULL, order_id TEXT NOT NULL, "
            "day TEXT NOT NULL, amount REAL NOT NULL, PRIMARY KEY(store_id, order_id))"
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


def _day(payload: dict) -> str:
    raw = str(payload.get("occurredAt") or "")
    if len(raw) >= 10:
        return raw[:10]
    return datetime.now(timezone.utc).strftime("%Y-%m-%d")


def _touch(c: sqlite3.Connection, sid: str, day: str) -> None:
    c.execute("INSERT OR IGNORE INTO daily(store_id,day) VALUES(?,?)", (sid, day))


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


# --------------------------------------------------------- ingest (events)

@app.post("/hooks/order-paid")
async def hook_paid(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    sid = _store(request)
    oid = str(d.get("orderId") or d.get("id") or "").strip()
    amount = float(d.get("amount") or 0)
    day = _day(d)
    if oid:
        with closing(connect()) as c:
            if c.execute("SELECT 1 FROM counted_orders WHERE store_id=? AND order_id=?", (sid, oid)).fetchone() is None:
                c.execute("INSERT INTO counted_orders(store_id,order_id,day,amount) VALUES(?,?,?,?)", (sid, oid, day, amount))
                _touch(c, sid, day)
                c.execute("UPDATE daily SET orders=orders+1, revenue=revenue+? WHERE store_id=? AND day=?", (amount, sid, day))
                c.commit()
    return JSONResponse({"recorded": True})


@app.post("/hooks/order-refunded")
async def hook_refunded(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    sid = _store(request)
    oid = str(d.get("orderId") or "").strip()
    amount = float(d.get("amount") or 0)
    day = _day(d)
    with closing(connect()) as c:
        # Refund is booked on the day it happened; the counted order (if any) is
        # released so a later re-count of that order is possible and correct.
        if oid:
            c.execute("DELETE FROM counted_orders WHERE store_id=? AND order_id=?", (sid, oid))
        _touch(c, sid, day)
        c.execute("UPDATE daily SET refunds=refunds+? WHERE store_id=? AND day=?", (amount, sid, day))
        c.commit()
    return JSONResponse({"recorded": True})


@app.post("/hooks/customer-registered")
async def hook_registered(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    sid = _store(request)
    day = _day(d)
    with closing(connect()) as c:
        _touch(c, sid, day)
        c.execute("UPDATE daily SET new_customers=new_customers+1 WHERE store_id=? AND day=?", (sid, day))
        c.commit()
    return JSONResponse({"recorded": True})


@app.post("/hooks/{_rest:path}")
async def hook_other(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"recorded": False, "reason": "not a reporting event"})


# ----------------------------------------------------------------- reports

def _range(request: Request) -> tuple[str, str]:
    to = request.query_params.get("to", "").strip() or datetime.now(timezone.utc).strftime("%Y-%m-%d")
    frm = request.query_params.get("from", "").strip()
    if not frm:
        frm = (datetime.strptime(to, "%Y-%m-%d") - timedelta(days=29)).strftime("%Y-%m-%d")
    return frm, to


def _series(c: sqlite3.Connection, sid: str, frm: str, to: str) -> list[dict]:
    rows = {r["day"]: r for r in c.execute(
        "SELECT day, orders, revenue, refunds, new_customers FROM daily WHERE store_id=? AND day BETWEEN ? AND ? ORDER BY day",
        (sid, frm, to),
    ).fetchall()}
    out = []
    try:
        start = datetime.strptime(frm, "%Y-%m-%d")
        end = datetime.strptime(to, "%Y-%m-%d")
    except ValueError:
        return [dict(r) for r in rows.values()]
    d = start
    while d <= end:
        key = d.strftime("%Y-%m-%d")
        r = rows.get(key)
        net = (float(r["revenue"]) - float(r["refunds"])) if r else 0.0
        out.append({
            "day": key,
            "orders": int(r["orders"]) if r else 0,
            "revenue": float(r["revenue"]) if r else 0.0,
            "refunds": float(r["refunds"]) if r else 0.0,
            "net": round(net, 2),
            "newCustomers": int(r["new_customers"]) if r else 0,
        })
        d += timedelta(days=1)
    return out


def _totals(series: list[dict]) -> dict:
    orders = sum(x["orders"] for x in series)
    revenue = round(sum(x["revenue"] for x in series), 2)
    refunds = round(sum(x["refunds"] for x in series), 2)
    net = round(revenue - refunds, 2)
    return {"orders": orders, "revenue": revenue, "refunds": refunds, "net": net,
            "newCustomers": sum(x["newCustomers"] for x in series),
            "averageOrderValue": round(net / orders, 2) if orders else 0.0}


@app.get("/api/v1/admin/report")
async def report(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    frm, to = _range(request)
    with closing(connect()) as c:
        series = _series(c, _store(request), frm, to)
    return JSONResponse({"from": frm, "to": to, "totals": _totals(series), "series": series})


@app.get("/api/v1/admin/export")
async def export(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    frm, to = _range(request)
    with closing(connect()) as c:
        series = _series(c, _store(request), frm, to)
    buf = io.StringIO()
    buf.write("day,orders,revenue,refunds,net,new_customers\n")
    for x in series:
        buf.write(f"{x['day']},{x['orders']},{x['revenue']},{x['refunds']},{x['net']},{x['newCustomers']}\n")
    return PlainTextResponse(buf.getvalue(), media_type="text/csv",
                             headers={"Content-Disposition": f'attachment; filename="report-{frm}-to-{to}.csv"'})


@app.get("/api/v1/admin/summary")
async def admin_summary(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    today = datetime.now(timezone.utc)
    with closing(connect()) as c:
        s7 = _totals(_series(c, sid, (today - timedelta(days=6)).strftime("%Y-%m-%d"), today.strftime("%Y-%m-%d")))
        s30 = _totals(_series(c, sid, (today - timedelta(days=29)).strftime("%Y-%m-%d"), today.strftime("%Y-%m-%d")))
    return JSONResponse({"last7": s7, "last30": s30})


@app.get("/api/v1/admin/{_rest:path}")
@app.get("/api/v1/admin/")
async def admin_dashboard(request: Request):
    if (e := _verify(request, b"")) is not None:
        return HTMLResponse(f"<p>Unauthorized: {e}</p>", status_code=401)
    sid = _store(request)
    today = datetime.now(timezone.utc)
    frm = (today - timedelta(days=13)).strftime("%Y-%m-%d")
    to = today.strftime("%Y-%m-%d")
    with closing(connect()) as c:
        series = _series(c, sid, frm, to)
    t = _totals(series)
    maxnet = max((x["net"] for x in series), default=0) or 1
    bars = "".join(
        f"<div class='bar' title='{x['day']}: {x['net']:,.0f}'>"
        f"<div class='fill' style='height:{max(2, round(x['net'] / maxnet * 100))}%'></div>"
        f"<div class='d'>{x['day'][5:]}</div></div>"
        for x in series
    )
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Analytics Reports</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}h2{{font-size:15px;margin:24px 0 8px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:8px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:130px}}
.card .n{{font-size:22px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
.chart{{display:flex;gap:6px;align-items:flex-end;height:160px;background:#fff;border-radius:12px;padding:16px;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
.bar{{flex:1;display:flex;flex-direction:column;justify-content:flex-end;align-items:center;height:100%}}
.bar .fill{{width:70%;background:#2b2d6e;border-radius:4px 4px 0 0;min-height:2px}}
.bar .d{{font-size:9px;color:#9ca3af;margin-top:4px;writing-mode:vertical-rl}}</style>
</head><body>
<h1>گزارش‌های تحلیلی</h1>
<p class="sub">دفتر روزبه‌روز فروش از جریان رویدادها؛ قابل فیلتر بر بازهٔ تاریخ و خروجی CSV (از مسیر export).</p>
<div class="cards">
  <div class="card"><div class="n">{t['orders']}</div><div class="l">سفارش (۱۴ روز)</div></div>
  <div class="card"><div class="n">{t['net']:,.0f}</div><div class="l">فروش خالص</div></div>
  <div class="card"><div class="n">{t['refunds']:,.0f}</div><div class="l">بازگشت وجه</div></div>
  <div class="card"><div class="n">{t['averageOrderValue']:,.0f}</div><div class="l">میانگین سفارش</div></div>
  <div class="card"><div class="n">{t['newCustomers']}</div><div class="l">مشتری تازه</div></div>
</div>
<h2>فروش خالص روزانه (۱۴ روز)</h2>
<div class="chart">{bars}</div>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": datetime.now(timezone.utc).isoformat()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Analytics Reports Service")
