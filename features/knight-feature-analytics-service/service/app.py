"""
Analytics as an external KNIGHT service.

One deployment serves every store entitled to `analytics-core` 2.x. A store never
loads this code: it forwards its lifecycle events here over HTTP and proxies the
merchant's analytics screen through, so the runtime the store is written in — a
Django shop, a .NET shop — stops mattering (docs/adr/0033-api-driven-features.md).

Every request except the health probe is signed by the store's ServiceProxy with
the shared secret, HMAC-SHA256 over

    METHOD \n path \n timestamp \n nonce \n sha256hex(body)

and the signature arrives as `X-Knight-Signature: sha256=<hex>`. We rebuild that
string from what we received and reject anything that does not match, is outside
the clock-skew window, or is signed with another key. Data is partitioned by the
`X-Knight-Store` header: this service holds many stores and one never sees
another's numbers.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import sqlite3
import time
from contextlib import closing
from datetime import datetime, timezone

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse

VERSION = "2.0.0"
DB_PATH = os.environ.get("ANALYTICS_DB_PATH", "/data/analytics.db")
SECRET = os.environ.get("ANALYTICS_SERVICE_SECRET", "")
SKEW_DEFAULT = 300

# The webhook path a store posts to -> the canonical event name we record it as.
EVENT_BY_PATH = {
    "/hooks/order-placed": "order.placed",
    "/hooks/order-paid": "order.paid",
    "/hooks/order-cancelled": "order.cancelled",
    "/hooks/order-refunded": "order.refunded",
    "/hooks/order-fulfilled": "order.fulfilled",
    "/hooks/cart-abandoned": "cart.abandoned",
    "/hooks/customer-registered": "customer.registered",
    "/hooks/customer-updated": "customer.updated",
    "/hooks/product-created": "product.created",
    "/hooks/product-updated": "product.updated",
    "/hooks/product-stock-changed": "product.stock_changed",
}

app = FastAPI(title="KNIGHT Analytics Service", version=VERSION)


def _connect() -> sqlite3.Connection:
    connection = sqlite3.connect(DB_PATH)
    connection.row_factory = sqlite3.Row
    return connection


def _init_db() -> None:
    os.makedirs(os.path.dirname(DB_PATH) or ".", exist_ok=True)
    with closing(_connect()) as connection:
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS events (
                store_id    TEXT NOT NULL,
                event_id    TEXT,
                type        TEXT NOT NULL,
                subject     TEXT,
                occurred_at TEXT NOT NULL,
                day         TEXT NOT NULL,
                received_at TEXT NOT NULL
            )
            """
        )
        # Idempotency for at-least-once delivery: a (store, event id) is recorded
        # at most once. Events without an id (older publishers) fall through and
        # are always recorded, which is the safe side for a counter.
        connection.execute(
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_events_store_eventid "
            "ON events(store_id, event_id) WHERE event_id IS NOT NULL"
        )
        connection.execute("CREATE INDEX IF NOT EXISTS ix_events_store_day ON events(store_id, day)")
        connection.commit()


_init_db()


def _verify(request: Request, body: bytes) -> str | None:
    """Return an error string, or None when the signature is good."""
    if not SECRET:
        return "the service has no shared secret configured"

    header = request.headers.get("x-knight-signature", "")
    timestamp = request.headers.get("x-knight-timestamp", "")
    nonce = request.headers.get("x-knight-nonce", "")
    if not header.startswith("sha256=") or not timestamp or not nonce:
        return "missing or malformed signature headers"

    try:
        skew = int(request.headers.get("x-knight-skew-seconds", SKEW_DEFAULT))
        sent_at = int(timestamp)
    except ValueError:
        return "unparseable timestamp or skew"

    if abs(int(time.time()) - sent_at) > max(skew, 0):
        return "the request is outside the clock-skew window"

    digest = hashlib.sha256(body).hexdigest()
    message = "\n".join([request.method.upper(), request.url.path, timestamp, nonce, digest])
    expected = hmac.new(SECRET.encode("utf-8"), message.encode("utf-8"), hashlib.sha256).hexdigest()

    if not hmac.compare_digest(expected, header[len("sha256="):]):
        return "the signature does not match"

    return None


def _store_id(request: Request) -> str:
    return request.headers.get("x-knight-store", "").strip()


@app.get("/healthz")
def healthz() -> JSONResponse:
    # Unauthenticated and touches nothing: it says the process is up, and the
    # store's agent is the only thing that calls it.
    return JSONResponse(
        {
            "status": "healthy",
            "version": VERSION,
            "checkedAt": datetime.now(timezone.utc).isoformat(),
        }
    )


@app.post("/hooks/{_rest:path}")
async def hook(request: Request) -> JSONResponse:
    body = await request.body()
    error = _verify(request, body)
    if error is not None:
        return JSONResponse({"error": error}, status_code=401)

    store_id = _store_id(request)
    if not store_id:
        return JSONResponse({"error": "no store id"}, status_code=400)

    event_type = EVENT_BY_PATH.get(request.url.path)
    if event_type is None:
        return JSONResponse({"error": f"unknown hook path {request.url.path}"}, status_code=404)

    try:
        payload = json.loads(body) if body else {}
        if not isinstance(payload, dict):
            payload = {}
    except json.JSONDecodeError:
        payload = {}

    event_id = str(payload.get("id") or payload.get("eventId") or "") or None
    subject = payload.get("subject") or payload.get("customerId") or payload.get("productId")
    occurred_at = str(payload.get("occurredAt") or payload.get("occurred_at") or "") or datetime.now(
        timezone.utc
    ).isoformat()
    day = occurred_at[:10] if len(occurred_at) >= 10 else datetime.now(timezone.utc).strftime("%Y-%m-%d")

    with closing(_connect()) as connection:
        try:
            connection.execute(
                "INSERT INTO events(store_id, event_id, type, subject, occurred_at, day, received_at) "
                "VALUES (?,?,?,?,?,?,?)",
                (
                    store_id,
                    event_id,
                    event_type,
                    str(subject) if subject is not None else None,
                    occurred_at,
                    day,
                    datetime.now(timezone.utc).isoformat(),
                ),
            )
            connection.commit()
            recorded = True
        except sqlite3.IntegrityError:
            # A duplicate of an event we already have. At-least-once means this is
            # expected, not an error.
            recorded = False

    return JSONResponse({"recorded": recorded, "type": event_type})


def _summary(store_id: str) -> dict:
    with closing(_connect()) as connection:
        total = connection.execute(
            "SELECT COUNT(*) AS n FROM events WHERE store_id = ?", (store_id,)
        ).fetchone()["n"]
        by_type = connection.execute(
            "SELECT type, COUNT(*) AS n FROM events WHERE store_id = ? GROUP BY type ORDER BY n DESC",
            (store_id,),
        ).fetchall()
        by_day = connection.execute(
            "SELECT day, COUNT(*) AS n FROM events WHERE store_id = ? GROUP BY day ORDER BY day DESC LIMIT 30",
            (store_id,),
        ).fetchall()
    return {
        "total": total,
        "byType": [{"type": row["type"], "count": row["n"]} for row in by_type],
        "byDay": [{"day": row["day"], "count": row["n"]} for row in by_day],
    }


@app.get("/api/v1/admin/summary")
async def admin_summary(request: Request) -> JSONResponse:
    error = _verify(request, b"")
    if error is not None:
        return JSONResponse({"error": error}, status_code=401)
    return JSONResponse(_summary(_store_id(request)))


@app.get("/api/v1/admin/{_rest:path}")
@app.get("/api/v1/admin/")
async def admin_dashboard(request: Request) -> HTMLResponse:
    error = _verify(request, b"")
    if error is not None:
        return HTMLResponse(f"<p>Unauthorized: {error}</p>", status_code=401)

    data = _summary(_store_id(request))
    rows = "".join(
        f"<tr><td>{item['type']}</td><td style='text-align:left'>{item['count']}</td></tr>"
        for item in data["byType"]
    ) or "<tr><td colspan='2'>هنوز رویدادی ثبت نشده است.</td></tr>"
    days = "".join(
        f"<tr><td>{item['day']}</td><td style='text-align:left'>{item['count']}</td></tr>"
        for item in data["byDay"]
    ) or "<tr><td colspan='2'>—</td></tr>"

    html = f"""<!doctype html>
<html lang="fa" dir="rtl">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>آنالیتیکس</title>
<style>
  body {{ font-family: system-ui, -apple-system, "Segoe UI", Vazirmatn, sans-serif; margin: 0; padding: 24px; color: #1a1a2e; background: #fafafc; }}
  h1 {{ font-size: 20px; margin: 0 0 4px; }}
  p.sub {{ color: #6b7280; margin: 0 0 20px; font-size: 13px; }}
  .total {{ font-size: 40px; font-weight: 700; margin: 8px 0 24px; }}
  .grid {{ display: grid; grid-template-columns: 1fr 1fr; gap: 24px; }}
  @media (max-width: 640px) {{ .grid {{ grid-template-columns: 1fr; }} }}
  table {{ width: 100%; border-collapse: collapse; background: #fff; border-radius: 10px; overflow: hidden; box-shadow: 0 1px 2px rgba(0,0,0,.06); }}
  th, td {{ padding: 10px 14px; border-bottom: 1px solid #eef0f4; font-size: 13px; }}
  th {{ text-align: right; background: #f4f4f8; font-weight: 600; }}
  caption {{ text-align: right; font-weight: 600; padding: 8px 0; }}
</style>
</head>
<body>
  <h1>آنالیتیکس فروشگاه</h1>
  <p class="sub">رویدادهای فروشگاه، تحویل‌شده به‌صورت سرویس از طریق KNIGHT.</p>
  <div class="total">{data['total']} <span style="font-size:14px;color:#6b7280;font-weight:400">رویداد کل</span></div>
  <div class="grid">
    <table><caption>بر اساس نوع</caption><tr><th>نوع رویداد</th><th style="text-align:left">تعداد</th></tr>{rows}</table>
    <table><caption>۳۰ روز اخیر</caption><tr><th>روز</th><th style="text-align:left">تعداد</th></tr>{days}</table>
  </div>
</body>
</html>"""
    return HTMLResponse(html)


@app.get("/")
def root() -> PlainTextResponse:
    return PlainTextResponse("KNIGHT Analytics Service")
