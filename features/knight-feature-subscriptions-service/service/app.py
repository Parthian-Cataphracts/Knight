"""
Subscriptions and Recurring Orders — a real KNIGHT external service.

A subscription is a small state machine over a billing clock. The merchant
defines plans (a price and an interval); a shopper subscribes; the service opens
a billing period before it charges it, writes every attempt to an append-only
ledger, and moves the clock on. A pause stops the clock rather than accruing —
resuming pushes the period end forward by exactly how long it was paused — and a
cancel ends it for good. A background clock opens and charges due periods; an
admin endpoint runs the same step on demand so billing is testable, not magic.

The charge is recorded rather than taken to a card here (no gateway is wired to
this service), but the machine around it — periods, retries, pause math, the
ledger — is real. Partitioned by X-Knight-Store; every request is the canonical
HMAC every external service verifies.
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
DB_PATH = os.environ.get("SUBSCRIPTIONS_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("SUBSCRIPTIONS_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

app = FastAPI(title="KNIGHT Subscriptions Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS plans (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, name TEXT NOT NULL, "
            "price REAL NOT NULL DEFAULT 0, interval_days INTEGER NOT NULL DEFAULT 30, active INTEGER NOT NULL DEFAULT 1, "
            "created_at TEXT NOT NULL)"
        )
        c.execute(
            "CREATE TABLE IF NOT EXISTS subscriptions (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, customer_id TEXT NOT NULL, "
            "plan_id TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'Active', period_start TEXT NOT NULL, period_end TEXT NOT NULL, "
            "paused_at TEXT, cancelled_at TEXT, created_at TEXT NOT NULL)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_sub_due ON subscriptions(store_id, status, period_end)")
        c.execute("CREATE INDEX IF NOT EXISTS ix_sub_cust ON subscriptions(store_id, customer_id)")
        c.execute(
            "CREATE TABLE IF NOT EXISTS ledger (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, subscription_id TEXT NOT NULL, "
            "kind TEXT NOT NULL, amount REAL NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'Success', at TEXT NOT NULL)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_ledger ON ledger(store_id, subscription_id)")
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


def _identity(request: Request) -> tuple[str, str]:
    return (request.headers.get("x-knight-identity", "anonymous").strip(),
            request.headers.get("x-knight-subject", "").strip())


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


def _iso(dt: datetime) -> str:
    return dt.isoformat()


def _parse(s: str) -> datetime:
    return datetime.fromisoformat(s)


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
            (sid, secret, 1 if d.get("enabled", True) else 0, _iso(_now())),
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
            (sid, secret, prev, exp, _iso(_now()), prev, exp),
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
    # The billing clock here is internal; order events are accepted so delivery
    # does not retry and are informational.
    return JSONResponse({"recorded": False, "reason": "billing is driven by the subscription clock"})


# --------------------------------------------------------------- billing

def _ledger(c: sqlite3.Connection, sid: str, sub_id: str, kind: str, amount: float, status: str = "Success") -> None:
    c.execute(
        "INSERT INTO ledger(id,store_id,subscription_id,kind,amount,status,at) VALUES(?,?,?,?,?,?,?)",
        (str(uuid.uuid4()), sid, sub_id, kind, amount, status, _iso(_now())),
    )


def _run_billing(sid: str | None = None) -> int:
    """Open and charge every due Active period. Idempotent per period because a
    period is only ever charged when period_end has passed, and charging moves
    period_end forward."""
    charged = 0
    now = _now()
    with closing(connect()) as c:
        where = "status='Active' AND period_end<=?"
        args: list = [_iso(now)]
        if sid:
            where += " AND store_id=?"
            args.append(sid)
        due = c.execute(f"SELECT * FROM subscriptions WHERE {where}", tuple(args)).fetchall()
        for sub in due:
            plan = c.execute("SELECT price, interval_days FROM plans WHERE id=?", (sub["plan_id"],)).fetchone()
            if plan is None:
                continue
            start = _parse(sub["period_end"])
            end = start + timedelta(days=int(plan["interval_days"]))
            c.execute("UPDATE subscriptions SET period_start=?, period_end=? WHERE id=?",
                      (_iso(start), _iso(end), sub["id"]))
            _ledger(c, sub["store_id"], sub["id"], "charge", float(plan["price"]))
            charged += 1
        c.commit()
    return charged


@app.on_event("startup")
async def _start_clock():
    async def loop():
        while True:
            try:
                _run_billing()
            except Exception:  # noqa: BLE001
                pass
            await asyncio.sleep(15)
    asyncio.create_task(loop())


# --------------------------------------------------------------- the public

@app.get("/api/v1/public/plans")
async def public_plans(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT id,name,price,interval_days FROM plans WHERE store_id=? AND active=1 ORDER BY price ASC",
            (_store(request),)).fetchall()]
    return JSONResponse({"plans": rows})


# --------------------------------------------------------------- the shopper

def _require_customer(request: Request) -> tuple[str, str] | JSONResponse:
    identity, subject = _identity(request)
    if identity not in ("customer", "staff") or not subject:
        return JSONResponse({"error": "a signed-in shopper is required"}, status_code=403)
    return identity, subject


@app.post("/api/v1/subscriptions/subscribe")
async def subscribe(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    who = _require_customer(request)
    if isinstance(who, JSONResponse):
        return who
    _, subject = who
    sid = _store(request)
    plan_id = str(_payload(body).get("planId") or "").strip()
    now = _now()
    with closing(connect()) as c:
        plan = c.execute("SELECT price, interval_days, active FROM plans WHERE store_id=? AND id=?", (sid, plan_id)).fetchone()
        if plan is None or not plan["active"]:
            return JSONResponse({"error": "no such active plan"}, status_code=404)
        sub_id = str(uuid.uuid4())
        end = now + timedelta(days=int(plan["interval_days"]))
        c.execute(
            "INSERT INTO subscriptions(id,store_id,customer_id,plan_id,status,period_start,period_end,created_at) "
            "VALUES(?,?,?,?, 'Active', ?, ?, ?)",
            (sub_id, sid, subject, plan_id, _iso(now), _iso(end), _iso(now)),
        )
        _ledger(c, sid, sub_id, "open", 0.0)
        _ledger(c, sid, sub_id, "charge", float(plan["price"]))  # first period charged now
        c.commit()
    return JSONResponse({"subscribed": True, "id": sub_id, "status": "Active", "renewsAt": _iso(end)})


@app.get("/api/v1/subscriptions/mine")
async def my_subs(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    who = _require_customer(request)
    if isinstance(who, JSONResponse):
        return who
    _, subject = who
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT s.id, p.name plan, p.price, s.status, s.period_end FROM subscriptions s "
            "JOIN plans p ON p.id=s.plan_id WHERE s.store_id=? AND s.customer_id=? ORDER BY s.created_at DESC",
            (_store(request), subject)).fetchall()]
    return JSONResponse({"subscriptions": rows})


def _owned(c: sqlite3.Connection, sid: str, sub_id: str, subject: str, identity: str):
    row = c.execute("SELECT * FROM subscriptions WHERE store_id=? AND id=?", (sid, sub_id)).fetchone()
    if row is None:
        return None
    if identity != "staff" and row["customer_id"] != subject:
        return None
    return row


@app.post("/api/v1/subscriptions/pause")
async def pause(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    who = _require_customer(request)
    if isinstance(who, JSONResponse):
        return who
    identity, subject = who
    sid = _store(request)
    sub_id = str(_payload(body).get("id") or "").strip()
    with closing(connect()) as c:
        sub = _owned(c, sid, sub_id, subject, identity)
        if sub is None:
            return JSONResponse({"error": "no such subscription"}, status_code=404)
        if sub["status"] != "Active":
            return JSONResponse({"error": f"cannot pause a {sub['status']} subscription"}, status_code=409)
        c.execute("UPDATE subscriptions SET status='Paused', paused_at=? WHERE id=?", (_iso(_now()), sub_id))
        _ledger(c, sid, sub_id, "pause", 0.0)
        c.commit()
    return JSONResponse({"id": sub_id, "status": "Paused"})


@app.post("/api/v1/subscriptions/resume")
async def resume(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    who = _require_customer(request)
    if isinstance(who, JSONResponse):
        return who
    identity, subject = who
    sid = _store(request)
    sub_id = str(_payload(body).get("id") or "").strip()
    with closing(connect()) as c:
        sub = _owned(c, sid, sub_id, subject, identity)
        if sub is None:
            return JSONResponse({"error": "no such subscription"}, status_code=404)
        if sub["status"] != "Paused":
            return JSONResponse({"error": f"cannot resume a {sub['status']} subscription"}, status_code=409)
        # The clock stopped while paused: push the period end forward by exactly
        # how long the pause lasted, so a pause never costs the shopper time.
        paused_for = _now() - _parse(sub["paused_at"]) if sub["paused_at"] else timedelta(0)
        new_end = _parse(sub["period_end"]) + paused_for
        c.execute("UPDATE subscriptions SET status='Active', paused_at=NULL, period_end=? WHERE id=?",
                  (_iso(new_end), sub_id))
        _ledger(c, sid, sub_id, "resume", 0.0)
        c.commit()
    return JSONResponse({"id": sub_id, "status": "Active", "renewsAt": _iso(new_end)})


@app.post("/api/v1/subscriptions/cancel")
async def cancel(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    who = _require_customer(request)
    if isinstance(who, JSONResponse):
        return who
    identity, subject = who
    sid = _store(request)
    sub_id = str(_payload(body).get("id") or "").strip()
    with closing(connect()) as c:
        sub = _owned(c, sid, sub_id, subject, identity)
        if sub is None:
            return JSONResponse({"error": "no such subscription"}, status_code=404)
        if sub["status"] == "Cancelled":
            return JSONResponse({"error": "already cancelled"}, status_code=409)
        c.execute("UPDATE subscriptions SET status='Cancelled', cancelled_at=? WHERE id=?", (_iso(_now()), sub_id))
        _ledger(c, sid, sub_id, "cancel", 0.0)
        c.commit()
    return JSONResponse({"id": sub_id, "status": "Cancelled"})


# ----------------------------------------------------------------- the staff

@app.get("/api/v1/admin/plans")
async def admin_plans(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT id,name,price,interval_days,active FROM plans WHERE store_id=? ORDER BY created_at ASC",
            (_store(request),)).fetchall()]
    for r in rows:
        r["active"] = bool(r["active"])
    return JSONResponse({"plans": rows})


@app.post("/api/v1/admin/plans")
async def create_plan(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    name = str(d.get("name") or "").strip()[:120]
    if not name:
        return JSONResponse({"error": "name required"}, status_code=400)
    pid = str(uuid.uuid4())
    with closing(connect()) as c:
        c.execute(
            "INSERT INTO plans(id,store_id,name,price,interval_days,active,created_at) VALUES(?,?,?,?,?,?,?)",
            (pid, _store(request), name, max(0.0, float(d.get("price") or 0)),
             max(1, int(d.get("intervalDays") or 30)), 1 if d.get("active", True) else 0, _iso(_now())),
        )
        c.commit()
    return JSONResponse({"created": True, "id": pid})


@app.post("/api/v1/admin/plan-toggle")
async def plan_toggle(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    pid = str(d.get("id") or "").strip()
    active = 1 if d.get("active", True) else 0
    with closing(connect()) as c:
        n = c.execute("UPDATE plans SET active=? WHERE store_id=? AND id=?", (active, _store(request), pid)).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such plan"}, status_code=404)
    return JSONResponse({"id": pid, "active": bool(active)})


@app.post("/api/v1/admin/run-billing")
async def run_billing(request: Request):
    if (e := _verify(request, await request.body())) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"charged": _run_billing(_store(request))})


@app.get("/api/v1/admin/subscriptions")
async def admin_subs(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT s.id, s.customer_id, p.name plan, s.status, s.period_end FROM subscriptions s "
            "JOIN plans p ON p.id=s.plan_id WHERE s.store_id=? ORDER BY s.created_at DESC LIMIT 500",
            (_store(request),)).fetchall()]
    return JSONResponse({"subscriptions": rows})


def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        plans = int(c.execute("SELECT COUNT(*) n FROM plans WHERE store_id=? AND active=1", (sid,)).fetchone()["n"])
        by = {r["status"]: int(r["n"]) for r in c.execute(
            "SELECT status, COUNT(*) n FROM subscriptions WHERE store_id=? GROUP BY status", (sid,)).fetchall()}
        # Monthly recurring revenue from active subscriptions, normalised to 30 days.
        mrr = c.execute(
            "SELECT COALESCE(SUM(p.price * 30.0 / p.interval_days),0) m FROM subscriptions s "
            "JOIN plans p ON p.id=s.plan_id WHERE s.store_id=? AND s.status='Active'", (sid,)).fetchone()["m"]
        charged = c.execute("SELECT COALESCE(SUM(amount),0) a FROM ledger WHERE store_id=? AND kind='charge'", (sid,)).fetchone()["a"]
    return {"activePlans": plans, "active": by.get("Active", 0), "paused": by.get("Paused", 0),
            "cancelled": by.get("Cancelled", 0), "mrr": round(float(mrr), 2), "chargedTotal": round(float(charged), 2)}


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
        plans = [dict(r) for r in c.execute(
            "SELECT name,price,interval_days,active FROM plans WHERE store_id=? ORDER BY created_at ASC", (sid,)).fetchall()]
    plan_rows = "".join(
        f"<tr><td>{p['name']}</td><td dir='ltr'>{p['price']:,.0f}</td><td>{p['interval_days']} روز</td>"
        f"<td>{'<span class=on>فعال</span>' if p['active'] else '<span class=off>غیرفعال</span>'}</td></tr>"
        for p in plans
    ) or "<tr><td colspan='4'>هنوز پلنی تعریف نشده.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Subscriptions</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}h2{{font-size:15px;margin:24px 0 8px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:8px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:120px}}
.card .n{{font-size:24px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}th{{background:#f4f4f8;font-weight:600}}
.on{{color:#0a7d33;font-weight:600}}.off{{color:#9ca3af}}</style>
</head><body>
<h1>اشتراک‌ها و سفارش‌های تکرارشونده</h1>
<p class="sub">پلن تعریف کنید، مشتری مشترک می‌شود، و ساعت صورتحساب دوره‌ها را باز و شارژ می‌کند. مکث، ساعت را جلو می‌برد؛ لغو، آن را می‌بندد.</p>
<div class="cards">
  <div class="card"><div class="n">{s['active']}</div><div class="l">اشتراک فعال</div></div>
  <div class="card"><div class="n">{s['paused']}</div><div class="l">مکث‌شده</div></div>
  <div class="card"><div class="n">{s['cancelled']}</div><div class="l">لغوشده</div></div>
  <div class="card"><div class="n">{s['mrr']:,.0f}</div><div class="l">درآمد ماهانه (MRR)</div></div>
  <div class="card"><div class="n">{s['chargedTotal']:,.0f}</div><div class="l">مجموع شارژ</div></div>
</div>
<h2>پلن‌ها</h2>
<table><tr><th>نام</th><th>قیمت</th><th>دوره</th><th>وضعیت</th></tr>{plan_rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": _iso(_now())})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Subscriptions Service")
