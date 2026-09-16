"""
Shared runtime for KNIGHT external-service Features.

Every external_service Feature answers the same two callers the same way — KNIGHT
signing control calls with a per-Feature control secret, and stores signing their
webhooks and proxied requests with a per-store secret KNIGHT hands over. Getting
that verification right once, here, is the point: a Feature's own module then
declares only what is specific to it (its name and which events it records) and
inherits the security, the per-store partitioning and a working dashboard.

Signing scheme (both callers): HMAC-SHA256 over
`METHOD \n path \n timestamp \n nonce \n sha256hex(body)`, carried as
`X-Knight-Signature: sha256=<hex>` with `X-Knight-Timestamp`/`X-Knight-Nonce`.
KNIGHT's control routes (/knight/stores/register|rotate|revoke) verify against
`<PREFIX>_CONTROL_SECRET`; a store's requests verify against the per-store secret
delivered over those routes, looked up by `X-Knight-Store`.
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

# The store's whole event vocabulary, as canonical name -> the webhook path a
# manifest exposes it at. A generated Feature records all of them by default and
# its manifest forwards whichever subset it declares; recording one nobody sends
# simply never happens.
DEFAULT_EVENTS: dict[str, str] = {
    "order.placed": "/hooks/order-placed",
    "order.paid": "/hooks/order-paid",
    "order.cancelled": "/hooks/order-cancelled",
    "order.refunded": "/hooks/order-refunded",
    "order.fulfilled": "/hooks/order-fulfilled",
    "cart.abandoned": "/hooks/cart-abandoned",
    "customer.registered": "/hooks/customer-registered",
    "customer.updated": "/hooks/customer-updated",
    "product.created": "/hooks/product-created",
    "product.updated": "/hooks/product-updated",
    "product.stock_changed": "/hooks/product-stock-changed",
}

SKEW_DEFAULT = 300


def create_app(
    display_name: str,
    events: dict[str, str] | None = None,
    *,
    env_prefix: str = "SERVICE",
    version: str = "2.0.0",
) -> FastAPI:
    """
    Build a ready-to-run external-service app.

    display_name: shown on the dashboard.
    events: canonical name -> webhook path; defaults to the full catalogue.
    env_prefix: `<PREFIX>_CONTROL_SECRET` and `<PREFIX>_DB_PATH` are read from the
        environment. The scaffolder sets it per Feature so two services on one
        host never share a secret variable.
    """
    events = events or dict(DEFAULT_EVENTS)
    event_by_path = {path: name for name, path in events.items()}
    control_secret = os.environ.get(f"{env_prefix}_CONTROL_SECRET", "")
    db_path = os.environ.get(f"{env_prefix}_DB_PATH", "/data/service.db")

    app = FastAPI(title=f"KNIGHT {display_name} Service", version=version)

    def connect() -> sqlite3.Connection:
        connection = sqlite3.connect(db_path)
        connection.row_factory = sqlite3.Row
        return connection

    def init_db() -> None:
        os.makedirs(os.path.dirname(db_path) or ".", exist_ok=True)
        with closing(connect()) as c:
            c.execute(
                "CREATE TABLE IF NOT EXISTS events (store_id TEXT NOT NULL, event_id TEXT, "
                "type TEXT NOT NULL, subject TEXT, occurred_at TEXT NOT NULL, day TEXT NOT NULL, "
                "received_at TEXT NOT NULL)"
            )
            c.execute(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_events_store_eventid "
                "ON events(store_id, event_id) WHERE event_id IS NOT NULL"
            )
            c.execute("CREATE INDEX IF NOT EXISTS ix_events_store_day ON events(store_id, day)")
            c.execute(
                "CREATE TABLE IF NOT EXISTS store_secrets (store_id TEXT PRIMARY KEY, secret TEXT NOT NULL, "
                "prev_secret TEXT, prev_expires INTEGER, enabled INTEGER NOT NULL DEFAULT 1, updated_at TEXT NOT NULL)"
            )
            c.commit()

    init_db()

    def sign(secret: str, method: str, path: str, timestamp: str, nonce: str, body: bytes) -> str:
        digest = hashlib.sha256(body).hexdigest()
        message = "\n".join([method.upper(), path, timestamp, nonce, digest])
        return hmac.new(secret.encode(), message.encode(), hashlib.sha256).hexdigest()

    def check(request: Request, body: bytes, candidates: list[str]) -> str | None:
        header = request.headers.get("x-knight-signature", "")
        timestamp = request.headers.get("x-knight-timestamp", "")
        nonce = request.headers.get("x-knight-nonce", "")
        if not header.startswith("sha256=") or not timestamp or not nonce:
            return "missing or malformed signature headers"
        try:
            skew = int(request.headers.get("x-knight-skew-seconds", SKEW_DEFAULT))
            sent = int(timestamp)
        except ValueError:
            return "unparseable timestamp or skew"
        if abs(int(time.time()) - sent) > max(skew, 0):
            return "the request is outside the clock-skew window"
        presented = header[len("sha256="):]
        for secret in candidates:
            if secret and hmac.compare_digest(
                sign(secret, request.method, request.url.path, timestamp, nonce, body), presented
            ):
                return None
        return "the signature does not match"

    def store_id(request: Request) -> str:
        return request.headers.get("x-knight-store", "").strip()

    def store_candidates(sid: str) -> list[str]:
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

    # ------------------------------------------------------------ control plane

    @app.post("/knight/stores/register")
    async def register(request: Request):
        body = await request.body()
        if (err := check(request, body, [control_secret])) is not None:
            return JSONResponse({"error": err}, status_code=401)
        d = json.loads(body or b"{}")
        sid, secret = str(d.get("storeId") or "").strip(), str(d.get("secret") or "")
        if not sid or not secret:
            return JSONResponse({"error": "storeId and secret are required"}, status_code=400)
        with closing(connect()) as c:
            c.execute(
                "INSERT INTO store_secrets(store_id,secret,prev_secret,prev_expires,enabled,updated_at) "
                "VALUES(?,?,NULL,NULL,?,?) ON CONFLICT(store_id) DO UPDATE SET secret=excluded.secret, "
                "enabled=excluded.enabled, updated_at=excluded.updated_at",
                (sid, secret, 1 if d.get("enabled", True) else 0, datetime.now(timezone.utc).isoformat()),
            )
            c.commit()
        return JSONResponse({"registered": True, "storeId": sid})

    @app.post("/knight/stores/rotate")
    async def rotate(request: Request):
        body = await request.body()
        if (err := check(request, body, [control_secret])) is not None:
            return JSONResponse({"error": err}, status_code=401)
        d = json.loads(body or b"{}")
        sid, secret = str(d.get("storeId") or "").strip(), str(d.get("secret") or "")
        overlap = int(d.get("overlapSeconds") or 0)
        if not sid or not secret:
            return JSONResponse({"error": "storeId and secret are required"}, status_code=400)
        with closing(connect()) as c:
            cur = c.execute("SELECT secret FROM store_secrets WHERE store_id=?", (sid,)).fetchone()
            prev = cur["secret"] if cur else None
            exp = int(time.time()) + overlap if prev else None
            c.execute(
                "INSERT INTO store_secrets(store_id,secret,prev_secret,prev_expires,enabled,updated_at) "
                "VALUES(?,?,?,?,1,?) ON CONFLICT(store_id) DO UPDATE SET prev_secret=?, prev_expires=?, "
                "secret=excluded.secret, enabled=1, updated_at=excluded.updated_at",
                (sid, secret, prev, exp, datetime.now(timezone.utc).isoformat(), prev, exp),
            )
            c.commit()
        return JSONResponse({"rotated": True, "storeId": sid, "overlapSeconds": overlap})

    @app.post("/knight/stores/revoke")
    async def revoke(request: Request):
        body = await request.body()
        if (err := check(request, body, [control_secret])) is not None:
            return JSONResponse({"error": err}, status_code=401)
        d = json.loads(body or b"{}")
        sid = str(d.get("storeId") or "").strip()
        with closing(connect()) as c:
            c.execute("DELETE FROM store_secrets WHERE store_id=?", (sid,))
            c.commit()
        return JSONResponse({"revoked": True, "storeId": sid})

    # --------------------------------------------------------------- the store

    @app.get("/healthz")
    def healthz():
        return JSONResponse(
            {"status": "healthy", "version": version, "checkedAt": datetime.now(timezone.utc).isoformat()}
        )

    @app.post("/hooks/{_rest:path}")
    async def hook(request: Request):
        body = await request.body()
        sid = store_id(request)
        if not sid:
            return JSONResponse({"error": "no store id"}, status_code=400)
        if (err := check(request, body, store_candidates(sid))) is not None:
            return JSONResponse({"error": err}, status_code=401)
        event_type = event_by_path.get(request.url.path)
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
        occurred = str(payload.get("occurredAt") or payload.get("occurred_at") or "") or datetime.now(
            timezone.utc
        ).isoformat()
        day = occurred[:10] if len(occurred) >= 10 else datetime.now(timezone.utc).strftime("%Y-%m-%d")
        with closing(connect()) as c:
            try:
                c.execute(
                    "INSERT INTO events(store_id,event_id,type,subject,occurred_at,day,received_at) VALUES(?,?,?,?,?,?,?)",
                    (sid, event_id, event_type, str(subject) if subject is not None else None, occurred, day,
                     datetime.now(timezone.utc).isoformat()),
                )
                c.commit()
                recorded = True
            except sqlite3.IntegrityError:
                recorded = False
        return JSONResponse({"recorded": recorded, "type": event_type})

    def summary(sid: str) -> dict:
        with closing(connect()) as c:
            total = c.execute("SELECT COUNT(*) n FROM events WHERE store_id=?", (sid,)).fetchone()["n"]
            by_type = c.execute(
                "SELECT type, COUNT(*) n FROM events WHERE store_id=? GROUP BY type ORDER BY n DESC", (sid,)
            ).fetchall()
            by_day = c.execute(
                "SELECT day, COUNT(*) n FROM events WHERE store_id=? GROUP BY day ORDER BY day DESC LIMIT 30", (sid,)
            ).fetchall()
        return {
            "total": total,
            "byType": [{"type": r["type"], "count": r["n"]} for r in by_type],
            "byDay": [{"day": r["day"], "count": r["n"]} for r in by_day],
        }

    @app.get("/api/v1/admin/summary")
    async def admin_summary(request: Request):
        sid = store_id(request)
        if (err := check(request, b"", store_candidates(sid))) is not None:
            return JSONResponse({"error": err}, status_code=401)
        return JSONResponse(summary(sid))

    @app.get("/api/v1/admin/{_rest:path}")
    @app.get("/api/v1/admin/")
    async def admin_dashboard(request: Request):
        sid = store_id(request)
        if (err := check(request, b"", store_candidates(sid))) is not None:
            return HTMLResponse(f"<p>Unauthorized: {err}</p>", status_code=401)
        data = summary(sid)
        rows = "".join(
            f"<tr><td>{i['type']}</td><td style='text-align:left'>{i['count']}</td></tr>" for i in data["byType"]
        ) or "<tr><td colspan='2'>هنوز رویدادی ثبت نشده است.</td></tr>"
        days = "".join(
            f"<tr><td>{i['day']}</td><td style='text-align:left'>{i['count']}</td></tr>" for i in data["byDay"]
        ) or "<tr><td colspan='2'>—</td></tr>"
        html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>{display_name}</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}
.total{{font-size:40px;font-weight:700;margin:8px 0 24px}}.grid{{display:grid;grid-template-columns:1fr 1fr;gap:24px}}
@media(max-width:640px){{.grid{{grid-template-columns:1fr}}}}table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:10px 14px;border-bottom:1px solid #eef0f4;font-size:13px}}th{{text-align:right;background:#f4f4f8;font-weight:600}}caption{{text-align:right;font-weight:600;padding:8px 0}}</style>
</head><body><h1>{display_name}</h1><p class="sub">رویدادهای فروشگاه، تحویل‌شده به‌صورت سرویس از طریق KNIGHT.</p>
<div class="total">{data['total']} <span style="font-size:14px;color:#6b7280;font-weight:400">رویداد کل</span></div>
<div class="grid"><table><caption>بر اساس نوع</caption><tr><th>نوع رویداد</th><th style="text-align:left">تعداد</th></tr>{rows}</table>
<table><caption>۳۰ روز اخیر</caption><tr><th>روز</th><th style="text-align:left">تعداد</th></tr>{days}</table></div></body></html>"""
        return HTMLResponse(html)

    @app.get("/")
    def root():
        return PlainTextResponse(f"KNIGHT {display_name} Service")

    return app
