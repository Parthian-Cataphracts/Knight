"""
External Marketplaces — a real KNIGHT external service.

A shop that also sells on other marketplaces needs one place that knows which of
its products are listed where, and whether each listing is up to date. This is
that register: the merchant defines channels (a marketplace such as Amazon, eBay
or Digikala), maps products onto them, and runs a sync that pushes the pending
ones and records the result. When the store edits a product, its listings on
every channel drop to "pending" so the drift is visible and the next sync fixes
it.

There are no marketplace credentials wired to this store, so the push itself is
simulated — a listing moves to Listed and is stamped with a synthetic external
reference — but the channel model, the mapping, the drift detection and the sync
queue are real. Partitioned by X-Knight-Store; every request is the canonical
HMAC every external service verifies.
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
DB_PATH = os.environ.get("EXTERNAL_MARKETPLACES_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("EXTERNAL_MARKETPLACES_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

app = FastAPI(title="KNIGHT External Marketplaces Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS channels (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, name TEXT NOT NULL, "
            "kind TEXT NOT NULL DEFAULT '', active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL)"
        )
        c.execute(
            "CREATE TABLE IF NOT EXISTS listings (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, channel_id TEXT NOT NULL, "
            "product_id TEXT NOT NULL, title TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'Pending', "
            "external_ref TEXT, last_synced TEXT, created_at TEXT NOT NULL)"
        )
        c.execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_listing ON listings(store_id, channel_id, product_id)")
        c.execute("CREATE INDEX IF NOT EXISTS ix_listing_status ON listings(store_id, status)")
        # Product titles, so a listing can show something and the store's edits
        # can be reflected.
        c.execute(
            "CREATE TABLE IF NOT EXISTS products (store_id TEXT NOT NULL, product_id TEXT NOT NULL, "
            "title TEXT NOT NULL DEFAULT '', PRIMARY KEY(store_id, product_id))"
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


# --------------------------------------------------------- drift (events)

@app.post("/hooks/product-created")
@app.post("/hooks/product-updated")
async def hook_product(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    sid = _store(request)
    pid = str(d.get("productId") or d.get("subject") or "").strip()
    if pid:
        with closing(connect()) as c:
            c.execute(
                "INSERT INTO products(store_id,product_id,title) VALUES(?,?,?) "
                "ON CONFLICT(store_id,product_id) DO UPDATE SET title=excluded.title",
                (sid, pid, str(d.get("title") or "")),
            )
            # An edit in the store means every marketplace copy is now stale.
            c.execute(
                "UPDATE listings SET status='Pending' WHERE store_id=? AND product_id=? AND status='Listed'",
                (sid, pid),
            )
            c.commit()
    return JSONResponse({"tracked": True})


@app.post("/hooks/{_rest:path}")
async def hook_other(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"tracked": False, "reason": "not a listing event"})


# ----------------------------------------------------------------- channels

@app.get("/api/v1/admin/channels")
async def list_channels(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT id,name,kind,active FROM channels WHERE store_id=? ORDER BY created_at ASC", (_store(request),)).fetchall()]
    for r in rows:
        r["active"] = bool(r["active"])
    return JSONResponse({"channels": rows})


@app.post("/api/v1/admin/channel-create")
async def create_channel(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    name = str(d.get("name") or "").strip()[:120]
    if not name:
        return JSONResponse({"error": "name required"}, status_code=400)
    cid = str(uuid.uuid4())
    with closing(connect()) as c:
        c.execute("INSERT INTO channels(id,store_id,name,kind,active,created_at) VALUES(?,?,?,?,?,?)",
                  (cid, _store(request), name, str(d.get("kind") or "").strip()[:60], 1 if d.get("active", True) else 0, _now()))
        c.commit()
    return JSONResponse({"created": True, "id": cid})


# ----------------------------------------------------------------- listings

@app.post("/api/v1/admin/list-product")
async def list_product(request: Request):
    """Map a product onto a channel — it starts Pending until the next sync."""
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    channel_id = str(d.get("channelId") or "").strip()
    pid = str(d.get("productId") or "").strip()
    if not channel_id or not pid:
        return JSONResponse({"error": "channelId and productId are required"}, status_code=400)
    with closing(connect()) as c:
        if c.execute("SELECT 1 FROM channels WHERE store_id=? AND id=?", (sid, channel_id)).fetchone() is None:
            return JSONResponse({"error": "no such channel"}, status_code=404)
        title = c.execute("SELECT title FROM products WHERE store_id=? AND product_id=?", (sid, pid)).fetchone()
        lid = str(uuid.uuid4())
        try:
            c.execute(
                "INSERT INTO listings(id,store_id,channel_id,product_id,title,status,created_at) "
                "VALUES(?,?,?,?,?, 'Pending', ?)",
                (lid, sid, channel_id, pid, title["title"] if title else str(d.get("title") or ""), _now()),
            )
            c.commit()
        except sqlite3.IntegrityError:
            return JSONResponse({"error": "already listed on this channel"}, status_code=409)
    return JSONResponse({"listed": True, "id": lid, "status": "Pending"})


@app.post("/api/v1/admin/sync")
async def sync(request: Request):
    """Push every Pending listing to its (active) channel. The push is simulated:
    it stamps an external reference and marks the listing Listed. A listing on a
    disabled channel is left Pending, which is what a real gateway would do."""
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    synced = 0
    with closing(connect()) as c:
        pend = c.execute(
            "SELECT l.id, l.channel_id, ch.active FROM listings l JOIN channels ch ON ch.id=l.channel_id "
            "WHERE l.store_id=? AND l.status='Pending'", (sid,),
        ).fetchall()
        for l in pend:
            if not l["active"]:
                continue
            ref = f"MP-{l['channel_id'][:6]}-{uuid.uuid4().hex[:8]}"
            c.execute("UPDATE listings SET status='Listed', external_ref=?, last_synced=? WHERE id=?",
                      (ref, _now(), l["id"]))
            synced += 1
        c.commit()
    return JSONResponse({"synced": synced})


@app.get("/api/v1/admin/listings")
async def list_listings(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT l.id, l.product_id, l.title, l.status, l.external_ref, l.last_synced, ch.name AS channel "
            "FROM listings l JOIN channels ch ON ch.id=l.channel_id WHERE l.store_id=? ORDER BY l.created_at DESC LIMIT 500",
            (_store(request),)).fetchall()]
    return JSONResponse({"listings": rows})


def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        channels = int(c.execute("SELECT COUNT(*) n FROM channels WHERE store_id=? AND active=1", (sid,)).fetchone()["n"])
        by = {r["status"]: int(r["n"]) for r in c.execute(
            "SELECT status, COUNT(*) n FROM listings WHERE store_id=? GROUP BY status", (sid,)).fetchall()}
    return {"channels": channels, "listed": by.get("Listed", 0), "pending": by.get("Pending", 0),
            "error": by.get("Error", 0)}


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
        chans = [dict(r) for r in c.execute(
            "SELECT name,kind,active FROM channels WHERE store_id=? ORDER BY created_at ASC", (sid,)).fetchall()]
        lst = [dict(r) for r in c.execute(
            "SELECT l.product_id, l.title, l.status, l.external_ref, ch.name AS channel FROM listings l "
            "JOIN channels ch ON ch.id=l.channel_id WHERE l.store_id=? ORDER BY l.created_at DESC LIMIT 50", (sid,)).fetchall()]
    ch_rows = "".join(
        f"<tr><td>{c['name']}</td><td>{c['kind'] or '—'}</td>"
        f"<td>{'<span class=on>فعال</span>' if c['active'] else '<span class=off>غیرفعال</span>'}</td></tr>"
        for c in chans
    ) or "<tr><td colspan='3'>هنوز کانالی تعریف نشده.</td></tr>"
    st_fa = {"Pending": "در انتظار", "Listed": "منتشرشده", "Error": "خطا"}
    l_rows = "".join(
        f"<tr><td dir='ltr'>{(x['title'] or x['product_id'])[:24]}</td><td>{x['channel']}</td>"
        f"<td>{st_fa.get(x['status'], x['status'])}</td><td dir='ltr'>{x['external_ref'] or '—'}</td></tr>"
        for x in lst
    ) or "<tr><td colspan='4'>هنوز لیستینگی نیست.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>External Marketplaces</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}h2{{font-size:15px;margin:24px 0 8px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:8px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:120px}}
.card .n{{font-size:24px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}th{{background:#f4f4f8;font-weight:600}}
.on{{color:#0a7d33;font-weight:600}}.off{{color:#9ca3af}}</style>
</head><body>
<h1>بازارگاه‌های خارجی</h1>
<p class="sub">محصولات‌تان را روی کانال‌های دیگر (آمازون، دیجی‌کالا، …) نگاشت کنید و با یک همگام‌سازی منتشر کنید؛ ویرایش محصول در فروشگاه، لیستینگ‌ها را «در انتظار» می‌کند تا دوباره همگام شوند.</p>
<div class="cards">
  <div class="card"><div class="n">{s['channels']}</div><div class="l">کانال فعال</div></div>
  <div class="card"><div class="n">{s['listed']}</div><div class="l">منتشرشده</div></div>
  <div class="card"><div class="n">{s['pending']}</div><div class="l">در انتظار همگام‌سازی</div></div>
</div>
<h2>کانال‌ها</h2>
<table><tr><th>نام</th><th>نوع</th><th>وضعیت</th></tr>{ch_rows}</table>
<h2>لیستینگ‌ها</h2>
<table><tr><th>محصول</th><th>کانال</th><th>وضعیت</th><th>شناسهٔ بیرونی</th></tr>{l_rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": _now()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT External Marketplaces Service")
