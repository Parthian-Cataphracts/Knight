"""
Marketing Automation — a real KNIGHT external service.

The merchant writes rules of the form "when this happens, reach out": when a cart
is abandoned, when someone registers, when an order is paid. The store forwards
those events; this service matches each one against the active campaigns and
schedules a message to the customer, after an optional delay and at most once per
customer per campaign. A background dispatcher then marks due messages sent.

There is no real mail or SMS channel wired to this store, so "sent" is recorded
rather than delivered — but the automation itself is real: the triggers match,
the queue fills, the delay and the once-per-customer rule are enforced, and the
merchant sees the schedule on the screen. Swapping the simulated send for a real
channel is one function.

Partitioned by X-Knight-Store; every request is the canonical HMAC every
external service verifies.
"""

from __future__ import annotations

import asyncio
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
DB_PATH = os.environ.get("MARKETING_AUTOMATION_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("MARKETING_AUTOMATION_CONTROL_SECRET", "")
SKEW_DEFAULT = 300
TRIGGERS = ("order.placed", "order.paid", "order.cancelled", "order.refunded",
            "order.fulfilled", "cart.abandoned", "customer.registered", "customer.updated")
CHANNELS = ("email", "sms", "webhook")
EVENT_PATHS = {
    "order-placed": "order.placed", "order-paid": "order.paid", "order-cancelled": "order.cancelled",
    "order-refunded": "order.refunded", "order-fulfilled": "order.fulfilled", "cart-abandoned": "cart.abandoned",
    "customer-registered": "customer.registered", "customer-updated": "customer.updated",
}

app = FastAPI(title="KNIGHT Marketing Automation Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS campaigns (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, name TEXT NOT NULL, "
            "trigger_event TEXT NOT NULL, channel TEXT NOT NULL DEFAULT 'email', message TEXT NOT NULL DEFAULT '', "
            "delay_minutes INTEGER NOT NULL DEFAULT 0, once_per_customer INTEGER NOT NULL DEFAULT 1, "
            "active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_camp ON campaigns(store_id, trigger_event, active)")
        c.execute(
            "CREATE TABLE IF NOT EXISTS runs (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, campaign_id TEXT NOT NULL, "
            "customer_id TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'Scheduled', scheduled_for TEXT NOT NULL, "
            "created_at TEXT NOT NULL, sent_at TEXT)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_runs_due ON runs(store_id, status, scheduled_for)")
        c.execute("CREATE INDEX IF NOT EXISTS ix_runs_dedup ON runs(store_id, campaign_id, customer_id)")
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


def _now() -> datetime:
    return datetime.now(timezone.utc)


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
            (sid, secret, 1 if d.get("enabled", True) else 0, _now().isoformat()),
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
            (sid, secret, prev, exp, _now().isoformat(), prev, exp),
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


# --------------------------------------------------------- triggers (events)

def _fire(sid: str, event: str, customer_id: str) -> int:
    if not customer_id:
        return 0
    scheduled = 0
    with closing(connect()) as c:
        camps = c.execute(
            "SELECT id, delay_minutes, once_per_customer FROM campaigns WHERE store_id=? AND trigger_event=? AND active=1",
            (sid, event),
        ).fetchall()
        for camp in camps:
            if camp["once_per_customer"] and c.execute(
                "SELECT 1 FROM runs WHERE store_id=? AND campaign_id=? AND customer_id=?",
                (sid, camp["id"], customer_id),
            ).fetchone():
                continue
            due = (_now() + timedelta(minutes=int(camp["delay_minutes"] or 0))).isoformat()
            c.execute(
                "INSERT INTO runs(id,store_id,campaign_id,customer_id,status,scheduled_for,created_at) "
                "VALUES(?,?,?,?, 'Scheduled', ?, ?)",
                (str(uuid.uuid4()), sid, camp["id"], customer_id, due, _now().isoformat()),
            )
            scheduled += 1
        c.commit()
    return scheduled


@app.post("/hooks/{event_path:path}")
async def hook(request: Request, event_path: str):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    event = EVENT_PATHS.get(event_path.strip("/"))
    if event is None:
        return JSONResponse({"scheduled": 0, "reason": "no campaign trigger for this event"})
    d = _payload(body)
    cid = str(d.get("customerId") or d.get("subject") or "").strip()
    n = _fire(_store(request), event, cid)
    return JSONResponse({"scheduled": n})


# --------------------------------------------------------- dispatcher (send)

def _dispatch_once() -> int:
    """Mark every due Scheduled run as Sent. The real send would go here."""
    now = _now().isoformat()
    with closing(connect()) as c:
        n = c.execute(
            "UPDATE runs SET status='Sent', sent_at=? WHERE status='Scheduled' AND scheduled_for<=?",
            (now, now),
        ).rowcount
        c.commit()
    return n


@app.on_event("startup")
async def _start_dispatcher():
    async def loop():
        while True:
            try:
                _dispatch_once()
            except Exception:  # noqa: BLE001 - a dispatcher must not die on one bad row
                pass
            await asyncio.sleep(15)
    asyncio.create_task(loop())


# ----------------------------------------------------------------- the staff

@app.get("/api/v1/admin/campaigns")
async def list_campaigns(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT id,name,trigger_event,channel,message,delay_minutes,once_per_customer,active,created_at "
            "FROM campaigns WHERE store_id=? ORDER BY created_at ASC", (_store(request),)).fetchall()]
    for r in rows:
        r["once_per_customer"], r["active"] = bool(r["once_per_customer"]), bool(r["active"])
    return JSONResponse({"campaigns": rows})


@app.post("/api/v1/admin/create")
async def create_campaign(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    name = str(d.get("name") or "").strip()[:120]
    trigger = str(d.get("trigger") or d.get("triggerEvent") or "").strip()
    channel = str(d.get("channel") or "email").strip()
    if not name or trigger not in TRIGGERS:
        return JSONResponse({"error": f"name and a trigger in {TRIGGERS} are required"}, status_code=400)
    if channel not in CHANNELS:
        channel = "email"
    cid = str(uuid.uuid4())
    with closing(connect()) as c:
        c.execute(
            "INSERT INTO campaigns(id,store_id,name,trigger_event,channel,message,delay_minutes,once_per_customer,active,created_at) "
            "VALUES(?,?,?,?,?,?,?,?,?,?)",
            (cid, _store(request), name, trigger, channel, str(d.get("message") or "")[:2000],
             max(0, int(d.get("delayMinutes") or 0)), 1 if d.get("oncePerCustomer", True) else 0,
             1 if d.get("active", True) else 0, _now().isoformat()),
        )
        c.commit()
    return JSONResponse({"created": True, "id": cid})


@app.post("/api/v1/admin/toggle")
async def toggle_campaign(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    cid = str(d.get("id") or "").strip()
    active = 1 if d.get("active", True) else 0
    with closing(connect()) as c:
        n = c.execute("UPDATE campaigns SET active=? WHERE store_id=? AND id=?",
                      (active, _store(request), cid)).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such campaign"}, status_code=404)
    return JSONResponse({"id": cid, "active": bool(active)})


@app.post("/api/v1/admin/delete")
async def delete_campaign(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    cid = str(_payload(body).get("id") or "").strip()
    with closing(connect()) as c:
        n = c.execute("DELETE FROM campaigns WHERE store_id=? AND id=?", (_store(request), cid)).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such campaign"}, status_code=404)
    return JSONResponse({"deleted": True, "id": cid})


@app.get("/api/v1/admin/runs")
async def list_runs(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    camp = request.query_params.get("campaign", "").strip()
    where = "store_id=?"
    args: list = [sid]
    if camp:
        where += " AND campaign_id=?"
        args.append(camp)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            f"SELECT id,campaign_id,customer_id,status,scheduled_for,sent_at FROM runs WHERE {where} "
            "ORDER BY created_at DESC LIMIT 200", tuple(args)).fetchall()]
    return JSONResponse({"runs": rows})


def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        camps = int(c.execute("SELECT COUNT(*) n FROM campaigns WHERE store_id=?", (sid,)).fetchone()["n"])
        active = int(c.execute("SELECT COUNT(*) n FROM campaigns WHERE store_id=? AND active=1", (sid,)).fetchone()["n"])
        by = {r["status"]: int(r["n"]) for r in c.execute(
            "SELECT status, COUNT(*) n FROM runs WHERE store_id=? GROUP BY status", (sid,)).fetchall()}
    return {"campaigns": camps, "active": active,
            "scheduled": by.get("Scheduled", 0), "sent": by.get("Sent", 0)}


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
        camps = [dict(r) for r in c.execute(
            "SELECT name,trigger_event,channel,delay_minutes,active FROM campaigns WHERE store_id=? ORDER BY created_at ASC",
            (sid,)).fetchall()]
    ev_fa = {"order.placed": "ثبت سفارش", "order.paid": "پرداخت سفارش", "order.cancelled": "لغو سفارش",
             "order.refunded": "بازگشت وجه", "order.fulfilled": "ارسال سفارش", "cart.abandoned": "رهاشدن سبد",
             "customer.registered": "ثبت‌نام مشتری", "customer.updated": "به‌روزرسانی مشتری"}
    ch_fa = {"email": "ایمیل", "sms": "پیامک", "webhook": "وب‌هوک"}
    rows = "".join(
        f"<tr><td>{c['name']}</td><td>{ev_fa.get(c['trigger_event'], c['trigger_event'])}</td>"
        f"<td>{ch_fa.get(c['channel'], c['channel'])}</td><td>{c['delay_minutes']} دقیقه</td>"
        f"<td>{'<span class=on>فعال</span>' if c['active'] else '<span class=off>غیرفعال</span>'}</td></tr>"
        for c in camps
    ) or "<tr><td colspan='5'>هنوز کمپینی تعریف نشده.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Marketing Automation</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}h2{{font-size:15px;margin:24px 0 8px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:8px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:120px}}
.card .n{{font-size:26px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}th{{background:#f4f4f8;font-weight:600}}
.on{{color:#0a7d33;font-weight:600}}.off{{color:#9ca3af}}</style>
</head><body>
<h1>اتوماسیون بازاریابی</h1>
<p class="sub">قانون‌هایی از جنس «وقتی این رخ داد، پیام بفرست». رویداد فروشگاه کمپین‌های فعال را می‌سنجد و پیام را — با تأخیر و حداکثر یک‌بار برای هر مشتری — زمان‌بندی می‌کند.</p>
<div class="cards">
  <div class="card"><div class="n">{s['campaigns']}</div><div class="l">کل کمپین‌ها</div></div>
  <div class="card"><div class="n">{s['active']}</div><div class="l">فعال</div></div>
  <div class="card"><div class="n">{s['scheduled']}</div><div class="l">در صف ارسال</div></div>
  <div class="card"><div class="n">{s['sent']}</div><div class="l">ارسال‌شده</div></div>
</div>
<h2>کمپین‌ها</h2>
<table><tr><th>نام</th><th>محرک</th><th>کانال</th><th>تأخیر</th><th>وضعیت</th></tr>{rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": _now().isoformat()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Marketing Automation Service")
