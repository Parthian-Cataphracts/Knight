"""
Multi-Location — a real KNIGHT external service.

A shop that grew past one address needs to tell shoppers where its branches are,
when they are open, and which of them a customer can collect an order from. This
service is the register of those places: the merchant adds a branch with an
address, opening hours and whether click-and-collect is on; the storefront reads
the public list to draw a "find a branch" map or a pickup picker.

It is deliberately not the stock ledger — advanced-inventory owns how much each
place holds. This owns where the places are and how to reach them. Partitioned
by X-Knight-Store; every request is the canonical HMAC every external service
verifies.
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
DB_PATH = os.environ.get("MULTI_LOCATION_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("MULTI_LOCATION_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

app = FastAPI(title="KNIGHT Multi-Location Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS locations (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, name TEXT NOT NULL, "
            "address TEXT NOT NULL DEFAULT '', city TEXT NOT NULL DEFAULT '', phone TEXT NOT NULL DEFAULT '', "
            "lat REAL, lng REAL, hours TEXT NOT NULL DEFAULT '', pickup INTEGER NOT NULL DEFAULT 0, "
            "active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_loc ON locations(store_id, active)")
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


@app.post("/hooks/{_rest:path}")
async def hook(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"recorded": False, "reason": "locations are managed by staff, not events"})


# ------------------------------------------------------------- serialisation

def _public(r: sqlite3.Row) -> dict:
    try:
        hours = json.loads(r["hours"]) if r["hours"] else {}
    except json.JSONDecodeError:
        hours = {}
    return {"id": r["id"], "name": r["name"], "address": r["address"], "city": r["city"],
            "phone": r["phone"], "lat": r["lat"], "lng": r["lng"], "hours": hours, "pickup": bool(r["pickup"])}


# --------------------------------------------------------------- the public

@app.get("/api/v1/public/locations")
async def public_locations(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    only_pickup = request.query_params.get("pickup", "").strip() in ("1", "true", "yes")
    with closing(connect()) as c:
        q = "SELECT * FROM locations WHERE store_id=? AND active=1"
        if only_pickup:
            q += " AND pickup=1"
        rows = c.execute(q + " ORDER BY name", (_store(request),)).fetchall()
    return JSONResponse({"locations": [_public(r) for r in rows]})


# ----------------------------------------------------------------- the staff

@app.get("/api/v1/admin/locations")
async def admin_locations(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = c.execute("SELECT * FROM locations WHERE store_id=? ORDER BY created_at ASC", (_store(request),)).fetchall()
    return JSONResponse({"locations": [{**_public(r), "active": bool(r["active"])} for r in rows]})


def _fields(d: dict) -> dict:
    hours = d.get("hours")
    return {
        "name": str(d.get("name") or "").strip()[:120],
        "address": str(d.get("address") or "").strip()[:400],
        "city": str(d.get("city") or "").strip()[:120],
        "phone": str(d.get("phone") or "").strip()[:60],
        "lat": float(d["lat"]) if d.get("lat") not in (None, "") else None,
        "lng": float(d["lng"]) if d.get("lng") not in (None, "") else None,
        "hours": json.dumps(hours) if isinstance(hours, (dict, list)) else str(hours or ""),
        "pickup": 1 if d.get("pickup") else 0,
        "active": 1 if d.get("active", True) else 0,
    }


@app.post("/api/v1/admin/create")
async def create_location(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    f = _fields(_payload(body))
    if not f["name"]:
        return JSONResponse({"error": "name required"}, status_code=400)
    lid = str(uuid.uuid4())
    with closing(connect()) as c:
        c.execute(
            "INSERT INTO locations(id,store_id,name,address,city,phone,lat,lng,hours,pickup,active,created_at) "
            "VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
            (lid, _store(request), f["name"], f["address"], f["city"], f["phone"], f["lat"], f["lng"],
             f["hours"], f["pickup"], f["active"], _now()),
        )
        c.commit()
    return JSONResponse({"created": True, "id": lid})


@app.post("/api/v1/admin/update")
async def update_location(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    lid = str(d.get("id") or "").strip()
    f = _fields(d)
    with closing(connect()) as c:
        n = c.execute(
            "UPDATE locations SET name=?,address=?,city=?,phone=?,lat=?,lng=?,hours=?,pickup=?,active=? "
            "WHERE store_id=? AND id=?",
            (f["name"], f["address"], f["city"], f["phone"], f["lat"], f["lng"], f["hours"],
             f["pickup"], f["active"], _store(request), lid),
        ).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such location"}, status_code=404)
    return JSONResponse({"updated": True, "id": lid})


@app.post("/api/v1/admin/delete")
async def delete_location(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    lid = str(_payload(body).get("id") or "").strip()
    with closing(connect()) as c:
        n = c.execute("DELETE FROM locations WHERE store_id=? AND id=?", (_store(request), lid)).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such location"}, status_code=404)
    return JSONResponse({"deleted": True, "id": lid})


def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        total = int(c.execute("SELECT COUNT(*) n FROM locations WHERE store_id=?", (sid,)).fetchone()["n"])
        active = int(c.execute("SELECT COUNT(*) n FROM locations WHERE store_id=? AND active=1", (sid,)).fetchone()["n"])
        pickup = int(c.execute("SELECT COUNT(*) n FROM locations WHERE store_id=? AND active=1 AND pickup=1", (sid,)).fetchone()["n"])
    return {"locations": total, "active": active, "pickup": pickup}


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
        rows = c.execute("SELECT * FROM locations WHERE store_id=? ORDER BY created_at ASC", (sid,)).fetchall()
    body_rows = "".join(
        f"<tr><td>{r['name']}</td><td>{r['city']}</td><td>{r['address']}</td><td dir='ltr'>{r['phone']}</td>"
        f"<td>{'بله' if r['pickup'] else 'خیر'}</td>"
        f"<td>{'<span class=on>فعال</span>' if r['active'] else '<span class=off>غیرفعال</span>'}</td></tr>"
        for r in rows
    ) or "<tr><td colspan='6'>هنوز شعبه‌ای ثبت نشده.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Multi-Location</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}h2{{font-size:15px;margin:24px 0 8px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:8px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:130px}}
.card .n{{font-size:26px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}th{{background:#f4f4f8;font-weight:600}}
.on{{color:#0a7d33;font-weight:600}}.off{{color:#9ca3af}}</style>
</head><body>
<h1>چند-شعبه</h1>
<p class="sub">فهرست شعبه‌ها با آدرس، ساعت کاری و امکان تحویل حضوری؛ ویترین همین فهرست عمومی را برای «شعبه‌ها» یا انتخاب محل تحویل می‌خواند.</p>
<div class="cards">
  <div class="card"><div class="n">{s['locations']}</div><div class="l">کل شعبه‌ها</div></div>
  <div class="card"><div class="n">{s['active']}</div><div class="l">فعال</div></div>
  <div class="card"><div class="n">{s['pickup']}</div><div class="l">تحویل حضوری</div></div>
</div>
<h2>شعبه‌ها</h2>
<table><tr><th>نام</th><th>شهر</th><th>آدرس</th><th>تلفن</th><th>تحویل حضوری</th><th>وضعیت</th></tr>{body_rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": _now()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Multi-Location Service")
