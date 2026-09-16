"""
Advanced Search — a real KNIGHT external service.

The store pushes its catalogue here as it changes: every product.created,
product.updated and product.stock_changed the store forwards becomes a row in a
real full-text index (SQLite FTS5). The storefront then asks this service for
ranked matches instead of running a LIKE over its own database, and the merchant
gets a screen showing how big the index is and a box to try a query against it.

Two audiences, told apart by the identity the store signs into each proxied
request: the public storefront (`anonymous`) searches, and staff (`staff`) see
the index dashboard. Everything is partitioned by X-Knight-Store, and every
request is the canonical HMAC every external service verifies.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import sqlite3
import time
from contextlib import closing
from datetime import datetime, timezone

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse

VERSION = "2.1.0"
DB_PATH = os.environ.get("ADVANCED_SEARCH_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("ADVANCED_SEARCH_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

app = FastAPI(title="KNIGHT Advanced Search Service", version=VERSION)


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
        # The source of truth for the index: one row per product per store. The
        # FTS table below is kept in step with it by triggers, so a delete or an
        # edit can never leave a stale entry a shopper would still find.
        c.execute(
            "CREATE TABLE IF NOT EXISTS products ("
            "rowid INTEGER PRIMARY KEY, store_id TEXT NOT NULL, product_id TEXT NOT NULL, "
            "title TEXT NOT NULL DEFAULT '', slug TEXT NOT NULL DEFAULT '', description TEXT NOT NULL DEFAULT '', "
            "sku TEXT NOT NULL DEFAULT '', price REAL NOT NULL DEFAULT 0, stock INTEGER NOT NULL DEFAULT 0, "
            "published INTEGER NOT NULL DEFAULT 1, updated_at TEXT NOT NULL, "
            "UNIQUE(store_id, product_id))"
        )
        c.execute(
            "CREATE VIRTUAL TABLE IF NOT EXISTS products_fts USING fts5("
            "title, description, sku, content='products', content_rowid='rowid', tokenize='unicode61')"
        )
        for stmt in (
            "CREATE TRIGGER IF NOT EXISTS products_ai AFTER INSERT ON products BEGIN "
            "INSERT INTO products_fts(rowid,title,description,sku) VALUES(new.rowid,new.title,new.description,new.sku); END",
            "CREATE TRIGGER IF NOT EXISTS products_ad AFTER DELETE ON products BEGIN "
            "INSERT INTO products_fts(products_fts,rowid,title,description,sku) VALUES('delete',old.rowid,old.title,old.description,old.sku); END",
            "CREATE TRIGGER IF NOT EXISTS products_au AFTER UPDATE ON products BEGIN "
            "INSERT INTO products_fts(products_fts,rowid,title,description,sku) VALUES('delete',old.rowid,old.title,old.description,old.sku); "
            "INSERT INTO products_fts(rowid,title,description,sku) VALUES(new.rowid,new.title,new.description,new.sku); END",
        ):
            c.execute(stmt)
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


# --------------------------------------------------------- indexing (events)

def _upsert(sid: str, d: dict) -> None:
    pid = str(d.get("productId") or d.get("subject") or "").strip()
    if not pid:
        return
    now = datetime.now(timezone.utc).isoformat()
    with closing(connect()) as c:
        c.execute(
            "INSERT INTO products(store_id,product_id,title,slug,description,sku,price,stock,published,updated_at) "
            "VALUES(?,?,?,?,?,?,?,?,?,?) "
            "ON CONFLICT(store_id,product_id) DO UPDATE SET title=excluded.title, slug=excluded.slug, "
            "description=excluded.description, sku=excluded.sku, price=excluded.price, stock=excluded.stock, "
            "published=excluded.published, updated_at=excluded.updated_at",
            (
                sid, pid,
                str(d.get("title") or ""), str(d.get("slug") or ""), str(d.get("description") or ""),
                str(d.get("sku") or ""), float(d.get("price") or 0), int(d.get("stock") or 0),
                1 if d.get("published", True) else 0, now,
            ),
        )
        c.commit()


@app.post("/hooks/product-created")
@app.post("/hooks/product-updated")
async def hook_product(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    _upsert(_store(request), _payload(body))
    return JSONResponse({"indexed": True})


@app.post("/hooks/product-stock-changed")
async def hook_stock(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    d = _payload(body)
    pid = str(d.get("productId") or d.get("subject") or "").strip()
    if pid:
        with closing(connect()) as c:
            c.execute(
                "UPDATE products SET stock=?, updated_at=? WHERE store_id=? AND product_id=?",
                (int(d.get("stock") or 0), datetime.now(timezone.utc).isoformat(), _store(request), pid),
            )
            c.commit()
    return JSONResponse({"indexed": True})


@app.post("/hooks/{_rest:path}")
async def hook_other(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    # The search index only cares about the catalogue; order and customer
    # events are accepted so delivery does not retry, and ignored.
    return JSONResponse({"indexed": False, "reason": "not a catalogue event"})


# --------------------------------------------------------------- the search

_TOKEN = re.compile(r"[^\w؀-ۿ]+", re.UNICODE)


def _match_query(q: str) -> str:
    """Turn a shopper's raw words into a safe FTS5 MATCH: each token quoted and
    given a prefix so "shir" finds "shirt", joined by OR for recall."""
    tokens = [t for t in _TOKEN.split(q.strip()) if t]
    return " OR ".join(f'"{t}"*' for t in tokens[:12])


def _search(sid: str, q: str, limit: int, include_unavailable: bool) -> list[dict]:
    match = _match_query(q)
    if not match:
        return []
    avail = "" if include_unavailable else " AND p.stock > 0 AND p.published = 1"
    with closing(connect()) as c:
        rows = c.execute(
            "SELECT p.product_id, p.title, p.slug, p.price, p.stock, p.published, bm25(products_fts) AS rank "
            "FROM products_fts JOIN products p ON p.rowid = products_fts.rowid "
            f"WHERE products_fts MATCH ? AND p.store_id = ?{avail} "
            "ORDER BY rank LIMIT ?",
            (match, sid, max(1, min(limit, 50))),
        ).fetchall()
    return [
        {
            "productId": r["product_id"], "title": r["title"], "slug": r["slug"],
            "price": r["price"], "stock": r["stock"], "published": bool(r["published"]),
            "score": round(-float(r["rank"]), 3),
        }
        for r in rows
    ]


@app.get("/api/v1/public/search")
async def public_search(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    q = request.query_params.get("q", "").strip()
    try:
        limit = int(request.query_params.get("limit", 20))
    except ValueError:
        limit = 20
    results = _search(_store(request), q, limit, include_unavailable=False)
    return JSONResponse({"query": q, "count": len(results), "results": results})


# ----------------------------------------------------------------- the staff

def _stats(sid: str) -> dict:
    with closing(connect()) as c:
        total = c.execute("SELECT COUNT(*) n FROM products WHERE store_id=?", (sid,)).fetchone()["n"]
        live = c.execute(
            "SELECT COUNT(*) n FROM products WHERE store_id=? AND published=1 AND stock>0", (sid,)).fetchone()["n"]
        last = c.execute("SELECT MAX(updated_at) t FROM products WHERE store_id=?", (sid,)).fetchone()["t"]
    return {"indexed": int(total), "searchable": int(live), "lastIndexedAt": last}


@app.get("/api/v1/admin/summary")
async def admin_summary(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse(_stats(_store(request)))


@app.get("/api/v1/admin/search")
async def admin_search(request: Request):
    """A staff test search that also sees unpublished and out-of-stock rows, so
    the merchant can confirm a product is in the index before it goes live."""
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    q = request.query_params.get("q", "").strip()
    results = _search(_store(request), q, 50, include_unavailable=True)
    return JSONResponse({"query": q, "count": len(results), "results": results})


@app.get("/api/v1/admin/{_rest:path}")
@app.get("/api/v1/admin/")
async def admin_dashboard(request: Request):
    if (e := _verify(request, b"")) is not None:
        return HTMLResponse(f"<p>Unauthorized: {e}</p>", status_code=401)
    s = _stats(_store(request))
    last = (s["lastIndexedAt"] or "—")[:19].replace("T", " ")
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Advanced Search</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:24px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:150px}}
.card .n{{font-size:28px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
.box{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
input{{font:inherit;padding:9px 12px;border:1px solid #d7d9e0;border-radius:8px;width:60%;max-width:340px}}
button{{font:inherit;padding:9px 18px;border:0;border-radius:8px;background:#2b2d6e;color:#fff;cursor:pointer;margin-inline-start:8px}}
table{{width:100%;border-collapse:collapse;margin-top:16px}}th,td{{padding:8px 10px;border-bottom:1px solid #eef0f4;font-size:12px;text-align:right}}
th{{background:#f4f4f8;font-weight:600}}.muted{{color:#9ca3af}}</style>
</head><body>
<h1>جستجوی پیشرفته</h1>
<p class="sub">فروشگاه محصولاتش را با هر تغییر به این ایندکس می‌فرستد؛ ویترین نتایج را از همین‌جا رتبه‌بندی‌شده می‌گیرد.</p>
<div class="cards">
  <div class="card"><div class="n">{s['indexed']}</div><div class="l">محصول در ایندکس</div></div>
  <div class="card"><div class="n">{s['searchable']}</div><div class="l">قابل نمایش (موجود و منتشرشده)</div></div>
  <div class="card"><div class="n" style="font-size:15px">{last}</div><div class="l">آخرین به‌روزرسانی</div></div>
</div>
<div class="box">
  <div><input id="q" placeholder="یک عبارت را روی ایندکس امتحان کنید…" />
  <button onclick="run()">جستجو</button></div>
  <div id="out"><p class="muted">نتیجه‌ای هنوز نیست.</p></div>
</div>
<script>
async function run(){{
  const q=document.getElementById('q').value.trim(); const out=document.getElementById('out');
  if(!q){{out.innerHTML='<p class="muted">یک عبارت بنویسید.</p>';return;}}
  out.innerHTML='<p class="muted">در حال جستجو…</p>';
  try{{
    const r=await fetch('search?q='+encodeURIComponent(q)); const d=await r.json();
    if(!d.results||!d.results.length){{out.innerHTML='<p class="muted">چیزی پیدا نشد.</p>';return;}}
    let h='<table><tr><th>عنوان</th><th>قیمت</th><th>موجودی</th><th>وضعیت</th><th>امتیاز</th></tr>';
    for(const x of d.results){{h+='<tr><td>'+(x.title||x.productId)+'</td><td dir=ltr>'+x.price+'</td><td>'+x.stock+'</td><td>'+(x.published?'منتشرشده':'پیش‌نویس')+'</td><td dir=ltr>'+x.score+'</td></tr>';}}
    out.innerHTML=h+'</table>';
  }}catch(e){{out.innerHTML='<p class="muted">خطا در جستجو.</p>';}}
}}
document.getElementById('q').addEventListener('keydown',e=>{{if(e.key==='Enter')run();}});
</script>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": datetime.now(timezone.utc).isoformat()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Advanced Search Service")
