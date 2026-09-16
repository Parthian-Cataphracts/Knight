"""
AI Reports — a real KNIGHT external service.

The reporting features hand back numbers; this one reads those numbers and says
what they mean, in sentences a merchant can act on: revenue is up or down on last
week, this was the best day, new customers are slowing, the refund rate is
climbing. The insights are computed from the store's own event stream by explicit
rules, not guessed — "AI" here is the summarisation, and it is honest about being
arithmetic rather than a language model, because a wrong number dressed up as a
model is worse than a right one stated plainly.

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
from contextlib import closing
from datetime import datetime, timedelta, timezone

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse

VERSION = "2.0.0"
DB_PATH = os.environ.get("AI_REPORTS_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("AI_REPORTS_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

app = FastAPI(title="KNIGHT AI Reports Service", version=VERSION)


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
            "orders INTEGER NOT NULL DEFAULT 0, revenue REAL NOT NULL DEFAULT 0, refunds REAL NOT NULL DEFAULT 0, "
            "new_customers INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(store_id, day))"
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
    return raw[:10] if len(raw) >= 10 else datetime.now(timezone.utc).strftime("%Y-%m-%d")


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
    amount = float(d.get("amount") or 0)
    day = _day(d)
    with closing(connect()) as c:
        _touch(c, sid, day)
        c.execute("UPDATE daily SET refunds=refunds+? WHERE store_id=? AND day=?", (amount, sid, day))
        c.commit()
    return JSONResponse({"recorded": True})


@app.post("/hooks/customer-registered")
async def hook_registered(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    day = _day(_payload(body))
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


# ----------------------------------------------------------------- insights

def _window(c: sqlite3.Connection, sid: str, start: datetime, end: datetime) -> dict:
    row = c.execute(
        "SELECT COALESCE(SUM(orders),0) o, COALESCE(SUM(revenue),0) r, COALESCE(SUM(refunds),0) f, "
        "COALESCE(SUM(new_customers),0) n FROM daily WHERE store_id=? AND day>=? AND day<?",
        (sid, start.strftime("%Y-%m-%d"), end.strftime("%Y-%m-%d")),
    ).fetchone()
    net = float(row["r"]) - float(row["f"])
    return {"orders": int(row["o"]), "revenue": float(row["r"]), "refunds": float(row["f"]),
            "net": net, "new": int(row["n"]), "aov": (net / int(row["o"])) if row["o"] else 0.0}


def _pct(cur: float, prev: float) -> float | None:
    if prev == 0:
        return None
    return round((cur - prev) / prev * 100.0, 1)


def _insights(sid: str) -> list[dict]:
    today = datetime.now(timezone.utc)
    tomorrow = today + timedelta(days=1)
    out: list[dict] = []
    with closing(connect()) as c:
        this_week = _window(c, sid, today - timedelta(days=6), tomorrow)
        last_week = _window(c, sid, today - timedelta(days=13), today - timedelta(days=6))
        last30_start = today - timedelta(days=29)

        # Revenue trend.
        p = _pct(this_week["net"], last_week["net"])
        if p is None:
            out.append({"title": "روند فروش", "sentiment": "neutral",
                        "detail": f"فروش خالص هفت روز اخیر {this_week['net']:,.0f} بود؛ برای مقایسه هنوز دادهٔ کافی از هفتهٔ قبل نیست."})
        else:
            out.append({"title": "روند فروش", "sentiment": "up" if p >= 0 else "down",
                        "detail": f"فروش خالص هفت روز اخیر {this_week['net']:,.0f} است، "
                                  f"{'+' if p >= 0 else ''}{p}٪ نسبت به هفتهٔ پیش‌از‌آن."})

        # Best day in the last 30.
        best = c.execute(
            "SELECT day, (revenue-refunds) net FROM daily WHERE store_id=? AND day>=? ORDER BY net DESC LIMIT 1",
            (sid, last30_start.strftime("%Y-%m-%d")),
        ).fetchone()
        if best and float(best["net"]) > 0:
            out.append({"title": "بهترین روز (۳۰ روز اخیر)", "sentiment": "up",
                        "detail": f"پرفروش‌ترین روز {best['day']} با فروش خالص {float(best['net']):,.0f} بود."})

        # New customers trend.
        pc = _pct(this_week["new"], last_week["new"])
        if this_week["new"] or last_week["new"]:
            if pc is None:
                out.append({"title": "مشتریان تازه", "sentiment": "up" if this_week["new"] else "neutral",
                            "detail": f"این هفته {this_week['new']} مشتری تازه ثبت‌نام کرد."})
            else:
                out.append({"title": "مشتریان تازه", "sentiment": "up" if pc >= 0 else "down",
                            "detail": f"این هفته {this_week['new']} مشتری تازه، "
                                      f"{'+' if pc >= 0 else ''}{pc}٪ نسبت به هفتهٔ قبل."})

        # Refund rate over 30 days.
        m30 = _window(c, sid, last30_start, tomorrow)
        if m30["revenue"] > 0:
            rate = round(m30["refunds"] / m30["revenue"] * 100.0, 1)
            out.append({"title": "نرخ بازگشت وجه (۳۰ روز)", "sentiment": "down" if rate > 10 else "neutral",
                        "detail": f"{rate}٪ از فروش ناخالص بازگردانده شده"
                                  + ("؛ بالاتر از حد معمول است، ارزش بررسی دارد." if rate > 10 else ".")})

        # AOV movement.
        pa = _pct(this_week["aov"], last_week["aov"])
        if this_week["orders"] and pa is not None:
            out.append({"title": "میانگین ارزش سفارش", "sentiment": "up" if pa >= 0 else "down",
                        "detail": f"میانگین سفارش این هفته {this_week['aov']:,.0f}، "
                                  f"{'+' if pa >= 0 else ''}{pa}٪ نسبت به هفتهٔ قبل."})

    if not out:
        out.append({"title": "هنوز داده‌ای نیست", "sentiment": "neutral",
                    "detail": "به‌محض ثبت چند سفارش و مشتری، بینش‌ها این‌جا ساخته می‌شوند."})
    return out


@app.get("/api/v1/admin/insights")
async def insights(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"insights": _insights(_store(request))})


@app.get("/api/v1/admin/summary")
async def admin_summary(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"insights": len(_insights(_store(request)))})


@app.get("/api/v1/admin/{_rest:path}")
@app.get("/api/v1/admin/")
async def admin_dashboard(request: Request):
    if (e := _verify(request, b"")) is not None:
        return HTMLResponse(f"<p>Unauthorized: {e}</p>", status_code=401)
    ins = _insights(_store(request))
    colour = {"up": "#0a7d33", "down": "#c1121f", "neutral": "#6b7280"}
    icon = {"up": "▲", "down": "▼", "neutral": "•"}
    cards = "".join(
        f"<div class='ins'><div class='t'><span style='color:{colour[i['sentiment']]}'>{icon[i['sentiment']]}</span> {i['title']}</div>"
        f"<div class='d'>{i['detail']}</div></div>"
        for i in ins
    )
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>AI Reports</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}
.ins{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);margin-bottom:12px}}
.ins .t{{font-weight:700;font-size:14px;margin-bottom:4px}}.ins .d{{color:#374151;font-size:13px;line-height:1.7}}</style>
</head><body>
<h1>گزارش‌های هوشمند</h1>
<p class="sub">بینش‌های خودکار از دادهٔ فروشگاه شما — محاسبه‌شده از رویدادها، بی‌اغراق و قابل‌اتکا.</p>
{cards}
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": datetime.now(timezone.utc).isoformat()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT AI Reports Service")
