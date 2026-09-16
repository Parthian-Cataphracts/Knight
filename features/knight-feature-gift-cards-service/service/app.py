"""
Gift Cards and Store Credit — a real KNIGHT external service.

This one is money, so the ledger is the truth: there is no balance column
anywhere. A gift card's balance is the sum of its ledger rows (issued minus
redeemed); a customer's store credit is the sum of theirs (granted minus spent).
Every movement is one append-only row, which is what lets the numbers be audited
and never drift.

The merchant issues cards and grants credit from a real screen, staff redeem a
card or spend a customer's credit, and the balances are always recomputed from
the ledger. Security is the platform contract, identical to every external
service: KNIGHT's control calls are HMAC-signed with the per-Feature control
secret; each store's requests are signed with the per-store secret KNIGHT
delivered. Data is partitioned by X-Knight-Store.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import secrets
import sqlite3
import time
from contextlib import closing
from datetime import datetime, timezone

from fastapi import FastAPI, Request
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse

VERSION = "2.0.0"
DB_PATH = os.environ.get("GIFT_CARDS_DB_PATH", "/data/service.db")
CONTROL_SECRET = os.environ.get("GIFT_CARDS_CONTROL_SECRET", "")
SKEW_DEFAULT = 300
CURRENCY = os.environ.get("GIFT_CARDS_CURRENCY", "IRT")

app = FastAPI(title="KNIGHT Gift Cards Service", version=VERSION)


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
        # A gift card exists as an identity (its code) and a currency; its money
        # is entirely in the ledger.
        c.execute(
            "CREATE TABLE IF NOT EXISTS cards (store_id TEXT NOT NULL, code TEXT NOT NULL, currency TEXT NOT NULL, "
            "created_at TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, PRIMARY KEY (store_id, code))"
        )
        # The one source of truth. kind: gift_issue, gift_redeem, credit_grant,
        # credit_spend. account is the card code or the customer id.
        c.execute(
            "CREATE TABLE IF NOT EXISTS ledger (id INTEGER PRIMARY KEY AUTOINCREMENT, store_id TEXT NOT NULL, "
            "scope TEXT NOT NULL, account TEXT NOT NULL, kind TEXT NOT NULL, amount INTEGER NOT NULL, "
            "detail TEXT, event_id TEXT, at TEXT NOT NULL)"
        )
        c.execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_ledger_event ON ledger(store_id, event_id) WHERE event_id IS NOT NULL")
        c.execute("CREATE INDEX IF NOT EXISTS ix_ledger_acct ON ledger(store_id, scope, account)")
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


def _control(request: Request, body: bytes):
    return _check(request, body, [CONTROL_SECRET])


@app.post("/knight/stores/register")
async def register(request: Request):
    body = await request.body()
    if (e := _control(request, body)) is not None:
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
    if (e := _control(request, body)) is not None:
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
    if (e := _control(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = str(json.loads(body or b"{}").get("storeId") or "").strip()
    with closing(connect()) as c:
        c.execute("DELETE FROM store_secrets WHERE store_id=?", (sid,))
        c.commit()
    return JSONResponse({"revoked": True})


# ------------------------------------------------------------------- webhooks

@app.post("/hooks/{_rest:path}")
async def hook(request: Request):
    # Gift cards and store credit move by explicit action, not by lifecycle
    # events — auto-crediting a refund here would double it against the store's
    # own wallet refund. Accept the signed delivery and record nothing, so the
    # store's outbox sees success rather than retrying forever.
    body = await request.body()
    if (e := _check(request, body, _candidates(_store(request)))) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse({"recorded": False, "reason": "gift-cards acts on explicit issue/redeem, not events"})


# ------------------------------------------------------------- money helpers

def _balance(c: sqlite3.Connection, sid: str, scope: str, account: str) -> int:
    row = c.execute(
        "SELECT COALESCE(SUM(amount),0) n FROM ledger WHERE store_id=? AND scope=? AND account=?",
        (sid, scope, account),
    ).fetchone()
    return int(row["n"])


def _payload(body: bytes) -> dict:
    try:
        d = json.loads(body) if body else {}
        return d if isinstance(d, dict) else {}
    except json.JSONDecodeError:
        return {}


def _staff(request: Request, body: bytes = b"") -> str | None:
    return _check(request, body, _candidates(_store(request)))


# --------------------------------------------------------------- staff: cards

@app.post("/api/v1/admin/gift-cards/issue")
async def issue_card(request: Request):
    body = await request.body()
    if (e := _staff(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    value = int(d.get("value") or 0)
    if value <= 0:
        return JSONResponse({"error": "a positive value is required"}, status_code=400)
    code = (str(d.get("code") or "").strip() or _new_code()).upper()
    now = datetime.now(timezone.utc).isoformat()
    with closing(connect()) as c:
        try:
            c.execute(
                "INSERT INTO cards(store_id,code,currency,created_at,active) VALUES(?,?,?,?,1)",
                (sid, code, CURRENCY, now),
            )
        except sqlite3.IntegrityError:
            return JSONResponse({"error": "that code already exists"}, status_code=409)
        c.execute(
            "INSERT INTO ledger(store_id,scope,account,kind,amount,detail,at) VALUES(?,?,?,?,?,?,?)",
            (sid, "gift", code, "gift_issue", value, d.get("detail") or "issued", now),
        )
        c.commit()
    return JSONResponse({"issued": True, "code": code, "balance": value, "currency": CURRENCY})


@app.post("/api/v1/admin/gift-cards/redeem")
async def redeem_card(request: Request):
    body = await request.body()
    if (e := _staff(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    code = str(d.get("code") or "").strip().upper()
    amount = int(d.get("amount") or 0)
    if not code or amount <= 0:
        return JSONResponse({"error": "code and a positive amount are required"}, status_code=400)
    now = datetime.now(timezone.utc).isoformat()
    with closing(connect()) as c:
        card = c.execute("SELECT active FROM cards WHERE store_id=? AND code=?", (sid, code)).fetchone()
        if card is None:
            return JSONResponse({"error": "no such card"}, status_code=404)
        if not card["active"]:
            return JSONResponse({"error": "card is inactive"}, status_code=409)
        bal = _balance(c, sid, "gift", code)
        if amount > bal:
            return JSONResponse({"error": "insufficient balance", "balance": bal}, status_code=409)
        c.execute(
            "INSERT INTO ledger(store_id,scope,account,kind,amount,detail,at) VALUES(?,?,?,?,?,?,?)",
            (sid, "gift", code, "gift_redeem", -amount, d.get("detail") or "redeemed", now),
        )
        c.commit()
        return JSONResponse({"redeemed": amount, "code": code, "balance": _balance(c, sid, "gift", code)})


# ------------------------------------------------------- staff: store credit

@app.post("/api/v1/admin/credit/grant")
async def grant_credit(request: Request):
    body = await request.body()
    if (e := _staff(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    customer = str(d.get("customerId") or "").strip()
    amount = int(d.get("amount") or 0)
    if not customer or amount <= 0:
        return JSONResponse({"error": "customerId and a positive amount are required"}, status_code=400)
    now = datetime.now(timezone.utc).isoformat()
    with closing(connect()) as c:
        c.execute(
            "INSERT INTO ledger(store_id,scope,account,kind,amount,detail,at) VALUES(?,?,?,?,?,?,?)",
            (sid, "credit", customer, "credit_grant", amount, d.get("detail") or "granted", now),
        )
        c.commit()
        return JSONResponse({"granted": amount, "customerId": customer, "balance": _balance(c, sid, "credit", customer)})


@app.post("/api/v1/admin/credit/spend")
async def spend_credit(request: Request):
    body = await request.body()
    if (e := _staff(request, body)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    d = _payload(body)
    customer = str(d.get("customerId") or "").strip()
    amount = int(d.get("amount") or 0)
    if not customer or amount <= 0:
        return JSONResponse({"error": "customerId and a positive amount are required"}, status_code=400)
    now = datetime.now(timezone.utc).isoformat()
    with closing(connect()) as c:
        bal = _balance(c, sid, "credit", customer)
        if amount > bal:
            return JSONResponse({"error": "insufficient credit", "balance": bal}, status_code=409)
        c.execute(
            "INSERT INTO ledger(store_id,scope,account,kind,amount,detail,at) VALUES(?,?,?,?,?,?,?)",
            (sid, "credit", customer, "credit_spend", -amount, d.get("detail") or "spent", now),
        )
        c.commit()
        return JSONResponse({"spent": amount, "customerId": customer, "balance": _balance(c, sid, "credit", customer)})


# ----------------------------------------------------------------- read side

@app.get("/api/v1/admin/card")
async def card_lookup(request: Request):
    if (e := _staff(request)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    sid = _store(request)
    code = request.query_params.get("code", "").strip().upper()
    if not code:
        return JSONResponse({"error": "code required"}, status_code=400)
    with closing(connect()) as c:
        card = c.execute("SELECT currency, active, created_at FROM cards WHERE store_id=? AND code=?", (sid, code)).fetchone()
        if card is None:
            return JSONResponse({"error": "no such card"}, status_code=404)
        history = [dict(r) for r in c.execute(
            "SELECT kind,amount,detail,at FROM ledger WHERE store_id=? AND scope='gift' AND account=? ORDER BY id DESC LIMIT 50",
            (sid, code),
        ).fetchall()]
        return JSONResponse({"code": code, "balance": _balance(c, sid, "gift", code),
                             "currency": card["currency"], "active": bool(card["active"]), "history": history})


def _summary(sid: str) -> dict:
    with closing(connect()) as c:
        cards = int(c.execute("SELECT COUNT(*) n FROM cards WHERE store_id=?", (sid,)).fetchone()["n"])
        gift_out = int(c.execute("SELECT COALESCE(SUM(amount),0) n FROM ledger WHERE store_id=? AND scope='gift'", (sid,)).fetchone()["n"])
        credit_out = int(c.execute("SELECT COALESCE(SUM(amount),0) n FROM ledger WHERE store_id=? AND scope='credit'", (sid,)).fetchone()["n"])
        credit_holders = int(c.execute("SELECT COUNT(DISTINCT account) n FROM ledger WHERE store_id=? AND scope='credit'", (sid,)).fetchone()["n"])
        recent = [dict(r) for r in c.execute(
            "SELECT scope,account,kind,amount,at FROM ledger WHERE store_id=? ORDER BY id DESC LIMIT 20", (sid,)
        ).fetchall()]
    return {"cards": cards, "giftOutstanding": gift_out, "creditOutstanding": credit_out,
            "creditHolders": credit_holders, "currency": CURRENCY, "recent": recent}


@app.get("/api/v1/admin/summary")
async def admin_summary(request: Request):
    if (e := _staff(request)) is not None:
        return JSONResponse({"error": e}, status_code=401)
    return JSONResponse(_summary(_store(request)))


@app.get("/api/v1/admin/{_rest:path}")
@app.get("/api/v1/admin/")
async def admin_dashboard(request: Request):
    if (e := _staff(request)) is not None:
        return HTMLResponse(f"<p>Unauthorized: {e}</p>", status_code=401)
    d = _summary(_store(request))
    rows = "".join(
        f"<tr><td>{r['scope']}</td><td dir='ltr'>{r['account']}</td><td>{r['kind']}</td>"
        f"<td style='text-align:left'>{r['amount']:,}</td><td>{r['at'][:19].replace('T',' ')}</td></tr>"
        for r in d["recent"]
    ) or "<tr><td colspan='5'>هنوز فعالیتی ثبت نشده است.</td></tr>"
    html = f"""<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>Gift Cards</title>
<style>body{{font-family:system-ui,-apple-system,"Segoe UI",Vazirmatn,sans-serif;margin:0;padding:24px;color:#1a1a2e;background:#fafafc}}
h1{{font-size:20px;margin:0 0 4px}}p.sub{{color:#6b7280;margin:0 0 20px;font-size:13px}}
.cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:24px}}
.card{{background:#fff;border-radius:12px;padding:16px 20px;box-shadow:0 1px 2px rgba(0,0,0,.06);min-width:150px}}
.card .n{{font-size:28px;font-weight:700}}.card .l{{color:#6b7280;font-size:12px}}
table{{width:100%;border-collapse:collapse;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 1px 2px rgba(0,0,0,.06)}}
th,td{{padding:9px 12px;border-bottom:1px solid #eef0f4;font-size:12px}}th{{text-align:right;background:#f4f4f8;font-weight:600}}caption{{text-align:right;font-weight:600;padding:8px 0}}</style>
</head><body>
<h1>کارت هدیه و اعتبار فروشگاهی</h1>
<p class="sub">دفترکل منبع حقیقت است؛ موجودی هر کارت و هر مشتری از جمع همان دفترکل به دست می‌آید.</p>
<div class="cards">
  <div class="card"><div class="n">{d['cards']:,}</div><div class="l">کارت‌های صادرشده</div></div>
  <div class="card"><div class="n">{d['giftOutstanding']:,}</div><div class="l">مانده‌ی کارت هدیه ({d['currency']})</div></div>
  <div class="card"><div class="n">{d['creditOutstanding']:,}</div><div class="l">اعتبار فروشگاهی ({d['currency']})</div></div>
  <div class="card"><div class="n">{d['creditHolders']:,}</div><div class="l">مشتریان دارای اعتبار</div></div>
</div>
<table><caption>فعالیت اخیر</caption>
<tr><th>نوع</th><th>حساب</th><th>رویداد</th><th style="text-align:left">مبلغ</th><th>زمان</th></tr>{rows}</table>
</body></html>"""
    return HTMLResponse(html)


@app.get("/healthz")
def healthz():
    return JSONResponse({"status": "healthy", "version": VERSION, "checkedAt": datetime.now(timezone.utc).isoformat()})


@app.get("/")
def root():
    return PlainTextResponse("KNIGHT Gift Cards Service")


def _new_code() -> str:
    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    return "-".join("".join(secrets.choice(alphabet) for _ in range(4)) for _ in range(3))
