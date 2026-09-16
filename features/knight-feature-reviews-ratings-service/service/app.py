"""
Reviews and Ratings — a real KNIGHT external service.

Shoppers rate and review products, the merchant moderates and replies, and the
storefront shows what was approved. Unlike the admin-only features, this one has
three audiences, and the store tells the service which by the identity it signs
into every proxied request: a shopper (`customer`), the public (`anonymous`) or
staff. The service never sees a session — it trusts `X-Knight-Identity` and
`X-Knight-Subject`, because the store asserted them and signed the request with
the shared secret (the canonical HMAC every external service verifies).

Data is partitioned by X-Knight-Store. A review is moderated before it is public:
it starts Pending and only an Approved one is returned to shoppers.
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

VERSION = "2.1.0"
DB_PATH = os.environ.get("REVIEWS_RATINGS_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("REVIEWS_RATINGS_CONTROL_SECRET", "")
SKEW_DEFAULT = 300

app = FastAPI(title="KNIGHT Reviews and Ratings Service", version=VERSION)


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
            "CREATE TABLE IF NOT EXISTS reviews (id TEXT PRIMARY KEY, store_id TEXT NOT NULL, product_id TEXT NOT NULL, "
            "customer_id TEXT NOT NULL, rating INTEGER NOT NULL, body TEXT, status TEXT NOT NULL DEFAULT 'Pending', "
            "reply TEXT, created_at TEXT NOT NULL, moderated_at TEXT)"
        )
        c.execute("CREATE INDEX IF NOT EXISTS ix_reviews_product ON reviews(store_id, product_id, status)")
        c.execute("CREATE INDEX IF NOT EXISTS ix_reviews_status ON reviews(store_id, status)")
        # One review per customer per product: a second submission edits the first.
        c.execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_reviews_one ON reviews(store_id, product_id, customer_id)")
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


@app.post("/hooks/{_rest:path}")
async def hook(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"recorded": False, "reason": "reviews are submitted by shoppers, not events"})


# --------------------------------------------------------------- the shopper

@app.post("/api/v1/customer/submit")
async def submit(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    identity, subject = _identity(request)
    if identity not in ("customer", "staff") or not subject:
        return JSONResponse({"error": "a signed-in shopper is required"}, status_code=403)
    sid = _store(request)
    d = _payload(body)
    product = str(d.get("productId") or "").strip()
    rating = int(d.get("rating") or 0)
    text = str(d.get("body") or "").strip()[:4000]
    if not product or rating < 1 or rating > 5:
        return JSONResponse({"error": "productId and a rating 1..5 are required"}, status_code=400)
    now = datetime.now(timezone.utc).isoformat()
    with closing(connect()) as c:
        # A resubmission edits the shopper's own review and sends it back for
        # moderation, rather than stacking a second one.
        c.execute(
            "INSERT INTO reviews(id,store_id,product_id,customer_id,rating,body,status,created_at) "
            "VALUES(?,?,?,?,?,?, 'Pending', ?) "
            "ON CONFLICT(store_id,product_id,customer_id) DO UPDATE SET rating=excluded.rating, body=excluded.body, "
            "status='Pending', created_at=excluded.created_at, moderated_at=NULL, reply=NULL",
            (str(uuid.uuid4()), sid, product, subject, rating, text, now),
        )
        c.commit()
    return JSONResponse({"submitted": True, "status": "Pending"})


@app.get("/api/v1/customer/mine")
async def my_reviews(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    identity, subject = _identity(request)
    if identity not in ("customer", "staff") or not subject:
        return JSONResponse({"error": "a signed-in shopper is required"}, status_code=403)
    sid = _store(request)
    with closing(connect()) as c:
        rows = [dict(r) for r in c.execute(
            "SELECT product_id,rating,body,status,reply,created_at FROM reviews WHERE store_id=? AND customer_id=? ORDER BY created_at DESC",
            (sid, subject),
        ).fetchall()]
    return JSONResponse({"reviews": rows})


# ---------------------------------------------------------------- the public

@app.get("/api/v1/public/product")
async def public_reviews(request: Request):
    if (e := _verify(request, b"")) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    product = request.query_params.get("productId", "").strip()
    if not product:
        return JSONResponse({"error": "productId required"}, status_code=400)
    with closing(connect()) as c:
        rows = c.execute(
            "SELECT rating, body, reply, created_at FROM reviews WHERE store_id=? AND product_id=? AND status='Approved' ORDER BY created_at DESC LIMIT 100",
            (sid, product),
        ).fetchall()
        agg = c.execute(
            "SELECT COUNT(*) n, COALESCE(AVG(rating),0) avg FROM reviews WHERE store_id=? AND product_id=? AND status='Approved'",
            (sid, product),
        ).fetchone()
    return JSONResponse({
        "productId": product,
        "count": int(agg["n"]),
        "average": round(float(agg["avg"]), 2),
        "reviews": [{"rating": r["rating"], "body": r["body"], "reply": r["reply"], "at": r["created_at"]} for r in rows],
    })


# ----------------------------------------------------------------- the staff

@app.post("/api/v1/admin/moderate")
async def moderate(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    rid = str(d.get("id") or "").strip()
    action = str(d.get("action") or "").strip().lower()
    if action not in ("approve", "reject"):
        return JSONResponse({"error": "action must be approve or reject"}, status_code=400)
    status = "Approved" if action == "approve" else "Rejected"
    with closing(connect()) as c:
        n = c.execute(
            "UPDATE reviews SET status=?, moderated_at=? WHERE store_id=? AND id=?",
            (status, datetime.now(timezone.utc).isoformat(), sid, rid),
        ).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such review"}, status_code=404)
    return JSONResponse({"id": rid, "status": status})


@app.post("/api/v1/admin/reply")
async def reply(request: Request):
    body = await request.body()
    if (e := _verify(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    rid = str(d.get("id") or "").strip()
    text = str(d.get("reply") or "").strip()[:2000]
    with closing(connect()) as c:
        n = c.execute("UPDATE reviews SET reply=? WHERE store_id=? AND id=?", (text, sid, rid)).rowcount
        c.commit()
    if n == 0:
        return JSONResponse({"error": "no such review"}, status_code=404)
    return JSONResponse({"id": rid, "reply": text})


def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        by_status = {r["status"]: int(r["n"]) for r in c.execute(
            "SELECT status, COUNT(*) n FROM reviews WHERE store_id=? GROUP BY status", (sid,)).fetchall()}
        avg = c.execute("SELECT COALESCE(AVG(rating),0) a FROM reviews WHERE store_id=? AND status='Approved'", (sid,)).fetchone()["a"]
        pending = [dict(r) for r in c.execute(
            "SELECT id,product_id,customer_id,rating,body,created_at FROM reviews WHERE store_id=? AND status='Pending' ORDER BY created_at ASC LIMIT 50",
            (sid,)).fetchall()]
    return {"byStatus": by_status, "average": round(float(avg), 2), "pending": pending}


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
    d = _summary(_store(request))
    status_cards = "".join(
        f"<div class='card'><div class='n'>{d['byStatus'].get(s, 0)}</div><div class='l'>{label}</div></div>"
        for s, label in (("Pending", "در انتظار"), ("Approved", "تأییدشده"), ("Rejected", "ردشده"))
    )
    pending = "".join(
        f"<tr><td dir='ltr'>{r['product_id']}</td><td>{'★' * r['rating']}</td><td>{(r['body'] or '')[:120]}</td>"
        f"<td dir='ltr'>{r['id']}</td></tr>"
        for r in d["pending"]
    ) or "<tr><td colspan='4'>نظری در انتظار تأیید نیست.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Reviews</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:24px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:130px}}
.card .n{{font-size:28px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px}}th{{text-align:right;background:#f4f4f8;font-weight:600}}caption{{text-align:right;font-weight:600;padding:8px 0}}</style>
</head><body>
<h1>نظرات و امتیازها</h1>
<p class="sub">نظرها پیش از نمایش تأیید می‌شوند؛ میانگین امتیاز از نظرهای تأییدشده است.</p>
<div class="cards">{status_cards}
  <div class="card"><div class="n">{d['average']}</div><div class="l">میانگین امتیاز</div></div>
</div>
<table><caption>در انتظار تأیید</caption>
<tr><th>محصول</th><th>امتیاز</th><th>متن</th><th style="text-align:left">شناسه</th></tr>{pending}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": datetime.now(timezone.utc).isoformat()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Reviews and Ratings Service")
