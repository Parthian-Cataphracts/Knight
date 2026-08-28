# The subscriptions service

What is behind `subscriptions` 2.0.0 in KNIGHT's catalogue. Not a store and not a
Feature package — an ordinary Django application that many stores talk to
([`adr/0033`](../../docs/adr/0033-api-driven-features.md)).

## What changed from the package

Same domain, same state machine, same billing clock, same ledger. Two things are
different and both follow from one deployment serving every shop:

**It owns its database.** In 1.x this ran inside each store with that store's
database handle, adding its tables to that store's schema. Here it has its own,
and a store cannot reach it except over HTTP with a signature.

**Every row belongs to a store.** `Subscription.store`, and a reference that is
unique *within* a store rather than globally — two shops both numbering from
`SUB-1` is the normal case, and a global unique index would have made the second
shop's first subscription fail to create. Every request-driven function in
`services.py` takes the store as its first argument, and it is not optional: a
caller that forgot would fail to run rather than quietly read another shop's
book.

Per-store configuration is a column rather than a file: `Store.settings` and
`Store.secrets`, read through `subscriptions/config.py`, which keeps the
interface the package had so the domain moved across unchanged.

## The contract

Every request but `/healthz` carries an HMAC-SHA256 signature over

```
METHOD \n path \n timestamp \n nonce \n sha256(body)
```

under a secret that store was issued. **Under any secret it currently holds** —
a store has a set of them, not one, so a rotation overlaps rather than cutting
off everything in flight ([`adr/0034`](../../docs/adr/0034-a-shared-secret-has-a-lifetime.md)).

Four checks in this order, and the order matters:

1. **the store is known and enabled** — refused before any cryptography, so
   timing cannot reveal which stores exist;
2. **the timestamp is inside the window** — cheap, and it bounds everything below;
3. **the HMAC matches**, in fixed time;
4. **the nonce is unused** — last, because it is the only check that writes, and
   a failed request must not burn a legitimate store's nonce space.

`X-Knight-Identity` and `X-Knight-Subject` say who the store believes is asking.
They are believed **only because the signature already verified**. An unsigned
request naming a customer is an unauthenticated request naming a customer.

## Routes

| | |
|---|---|
| `POST /hooks/order-{placed,paid,cancelled,refunded}` | the four events the store forwards |
| `GET/POST /api/v1/subscriptions/…` | what the store's `subscriptions/` prefix proxies to |
| `GET /api/v1/admin/…` | what `admin/subscriptions/` proxies to, staff only |
| `POST /knight/stores/{register,rotate,revoke,describe}` | what KNIGHT may say, signed with the control plane's own secret |
| `GET /healthz` | liveness, unsigned |

There is no admin site, no login and no session endpoint. Identity is decided in
the store and asserted here; a second way in would be a second thing to get
wrong.

`/knight/…` is the one surface a store may not reach. It is signed with
`SUBSCRIPTIONS_CONTROL_SECRET`, which belongs to KNIGHT and to nobody else: a
store cannot prove it is a store before it has a secret, and issuing that secret
is what those routes are for. With that variable unset the whole surface refuses
everything — unconfigured is closed, never open. Nothing on it returns a secret;
it reports how many are live, which is what a reconciliation needs.

## Running it

```bash
pip install -r requirements.txt
SUBSCRIPTIONS_DEBUG=true python manage.py migrate
SUBSCRIPTIONS_DEBUG=true python manage.py runserver 8100
```

Stores are registered by KNIGHT, over `/knight/stores/register`. The command
below remains for an operator fixing something by hand, and it performs a
**rotation** rather than an overwrite — so running it against a store that is
already working does not cut off whatever that store has in flight:

```bash
python manage.py knight_store add --slug camden-coffee --store-id <uuid> --secret <shared secret>
python manage.py knight_store list          # slug, id, state, and how many secrets are live
python manage.py knight_store disable --slug camden-coffee
```

`list` shows a count and never a value. Two live secrets is a rotation in
progress; zero is a store that cannot say anything, and is the one worth
spotting.

## Housekeeping

Two tables grow for ever if nothing runs: used nonces, which are only useful
while a captured request's timestamp is still acceptable, and secrets nobody can
use any more, which keep their dates and lose their values after a month.

```bash
python manage.py knight_maintain                      # one pass
python manage.py knight_maintain --loop --every 3600  # what the compose sidecar runs
```

Safe at any interval and safe to run twice: every operation is over a cut-off,
and a second pass finds nothing left to do.

## Testing

```bash
python manage.py test
```

Fifty-three tests, and most of them are attacks: another store's secret, a body
altered after signing, a stale timestamp, a replay, a failed signature trying to
burn a nonce, a disabled store, a shopper reaching a staff route, and a shopper
reading another shopper's subscription by guessing its reference — and, on the
control plane, a store trying to rotate its own secret, a replayed registration,
and a rotation trying to give an expiring secret more life.
