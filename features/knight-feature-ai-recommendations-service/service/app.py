"""
AI Recommendations — a real KNIGHT external service.

Every paid order the store forwards carries its lines, and this service learns
two things from them: how often each product sells, and how often any two
products sell in the same basket. From that it answers the storefront's
"customers who bought this also bought…" — the co-occurrence ranking for a
product, falling back to the overall best-sellers when a product is too new to
have company. The learning is real (a co-occurrence matrix over the order
stream, counted once per order); the "AI" is that ranking, honestly a statistic
rather than a neural net.

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
from datetime import datetime, timezone
from itertools import combinations

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse

VERSION = "2.0.0"
DB_PATH = os.environ.get("AI_RECOMMENDATIONS_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("AI_RECOMMENDATIONS_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

app = FastAPI(title="KNIGHT AI Recommendations Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS popularity (store_id TEXT NOT NULL, product_id TEXT NOT NULL, "
            "purchases INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(store_id, product_id))"
        )
        c.execute(
            "CREATE TABLE IF NOT EXISTS cooccurrence (store_id TEXT NOT NULL, a TEXT NOT NULL, b TEXT NOT NULL, "
            "count INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(store_id, a, b))"
        )
        c.execute(
            "CREATE TABLE IF NOT EXISTS counted_orders (store_id TEXT NOT NULL, order_id TEXT NOT NULL, "
            "PRIMARY KEY(store_id, order_id))"
        )
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


# --------------------------------------------------------- learning (events)

@app.post("/hooks/product-created")
@app.post("/hooks/product-updated")
async def hook_product(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    pid = str(d.get("productId") or d.get("subject") or "").strip()
    if pid:
        with closing(connect()) as c:
            c.execute(
                "INSERT INTO products(store_id,product_id,title) VALUES(?,?,?) "
                "ON CONFLICT(store_id,product_id) DO UPDATE SET title=excluded.title",
                (_store(request), pid, str(d.get("title") or "")),
            )
            c.commit()
    return JSONResponse({"tracked": True})


@app.post("/hooks/order-paid")
async def hook_paid(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    sid = _store(request)
    oid = str(d.get("orderId") or d.get("id") or "").strip()
    items = d.get("items") or []
    products = []
    for it in items:
        pid = str(it.get("productId") or "").strip() if isinstance(it, dict) else ""
        if pid:
            products.append(pid)
    if not oid or not products:
        return JSONResponse({"learned": False, "reason": "no order id or no line items"})
    unique = sorted(set(products))
    with closing(connect()) as c:
        # Count each paid order once, matrix and popularity together.
        if c.execute("SELECT 1 FROM counted_orders WHERE store_id=? AND order_id=?", (sid, oid)).fetchone() is None:
            c.execute("INSERT INTO counted_orders(store_id,order_id) VALUES(?,?)", (sid, oid))
            for pid in unique:
                c.execute(
                    "INSERT INTO popularity(store_id,product_id,purchases) VALUES(?,?,1) "
                    "ON CONFLICT(store_id,product_id) DO UPDATE SET purchases=purchases+1",
                    (sid, pid),
                )
            for a, b in combinations(unique, 2):
                c.execute(
                    "INSERT INTO cooccurrence(store_id,a,b,count) VALUES(?,?,?,1) "
                    "ON CONFLICT(store_id,a,b) DO UPDATE SET count=count+1",
                    (sid, a, b),
                )
            c.commit()
    return JSONResponse({"learned": True})


@app.post("/hooks/{_rest:path}")
async def hook_other(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"learned": False, "reason": "not a learning event"})


# --------------------------------------------------------------- recommend

def _title(c: sqlite3.Connection, sid: str, pid: str) -> str:
    row = c.execute("SELECT title FROM products WHERE store_id=? AND product_id=?", (sid, pid)).fetchone()
    return row["title"] if row and row["title"] else ""


def _popular(c: sqlite3.Connection, sid: str, limit: int, exclude: str | None = None) -> list[dict]:
    rows = c.execute(
        "SELECT product_id, purchases FROM popularity WHERE store_id=? ORDER BY purchases DESC, product_id LIMIT ?",
        (sid, limit + (1 if exclude else 0)),
    ).fetchall()
    out = []
    for r in rows:
        if exclude and r["product_id"] == exclude:
            continue
        out.append({"productId": r["product_id"], "title": _title(c, sid, r["product_id"]),
                    "score": int(r["purchases"]), "reason": "best-seller"})
        if len(out) >= limit:
            break
    return out


def _also_bought(c: sqlite3.Connection, sid: str, pid: str, limit: int) -> list[dict]:
    rows = c.execute(
        "SELECT CASE WHEN a=? THEN b ELSE a END AS other, count FROM cooccurrence "
        "WHERE store_id=? AND (a=? OR b=?) ORDER BY count DESC LIMIT ?",
        (pid, sid, pid, pid, limit),
    ).fetchall()
    return [{"productId": r["other"], "title": _title(c, sid, r["other"]),
             "score": int(r["count"]), "reason": "bought together"} for r in rows]


@app.get("/api/v1/public/recommend")
async def recommend(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    pid = request.query_params.get("productId", "").strip()
    try:
        limit = min(max(int(request.query_params.get("limit", 5)), 1), 20)
    except ValueError:
        limit = 5
    with closing(connect()) as c:
        recs = _also_bought(c, sid, pid, limit) if pid else []
        if len(recs) < limit:
            # Top up with best-sellers the shopper is not already looking at or seeing.
            seen = {r["productId"] for r in recs} | ({pid} if pid else set())
            for extra in _popular(c, sid, limit + len(seen)):
                if extra["productId"] in seen:
                    continue
                recs.append(extra)
                if len(recs) >= limit:
                    break
    return JSONResponse({"productId": pid or None, "recommendations": recs[:limit]})


# ----------------------------------------------------------------- the staff

def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        prods = int(c.execute("SELECT COUNT(*) n FROM popularity WHERE store_id=?", (sid,)).fetchone()["n"])
        pairs = int(c.execute("SELECT COUNT(*) n FROM cooccurrence WHERE store_id=?", (sid,)).fetchone()["n"])
        orders = int(c.execute("SELECT COUNT(*) n FROM counted_orders WHERE store_id=?", (sid,)).fetchone()["n"])
    return {"productsLearned": prods, "pairs": pairs, "ordersLearned": orders}


@app.get("/api/v1/admin/summary")
async def admin_summary(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse(_summary(_store(request)))


@app.get("/api/v1/admin/top")
async def admin_top(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    with closing(connect()) as c:
        return JSONResponse({"top": _popular(c, _store(request), 20)})


@app.get("/api/v1/admin/{_rest:path}")
@app.get("/api/v1/admin/")
async def admin_dashboard(request: Request):
    if (e := _verify(request, b"")) is not None:
        return HTMLResponse(f"<p>Unauthorized: {e}</p>", status_code=401)
    sid = _store(request)
    s = _summary(sid)
    with closing(connect()) as c:
        top = _popular(c, sid, 15)
    rows = "".join(
        f"<tr><td dir='ltr'>{(t['title'] or t['productId'])[:28]}</td><td>{t['score']}</td></tr>" for t in top
    ) or "<tr><td colspan='2'>هنوز خریدی برای یادگیری نیست.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>AI Recommendations</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}h2{{font-size:15px;margin:24px 0 8px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:8px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:140px}}
.card .n{{font-size:26px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}th{{background:#f4f4f8;font-weight:600}}</style>
</head><body>
<h1>پیشنهادهای هوشمند</h1>
<p class="sub">از سبد سفارش‌های پرداخت‌شده یاد می‌گیرد: چه چیزی پرفروش است و چه چیزهایی با هم خریده می‌شوند. ویترین «مشتریانی که این را خریدند…» را از همین‌جا می‌گیرد.</p>
<div class="cards">
  <div class="card"><div class="n">{s['ordersLearned']}</div><div class="l">سفارش یادگرفته</div></div>
  <div class="card"><div class="n">{s['productsLearned']}</div><div class="l">محصول</div></div>
  <div class="card"><div class="n">{s['pairs']}</div><div class="l">جفت هم‌خرید</div></div>
</div>
<h2>پرفروش‌ترین‌ها</h2>
<table><tr><th>محصول</th><th>خرید</th></tr>{rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": _now()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT AI Recommendations Service")
