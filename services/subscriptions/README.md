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

under the secret that store was issued. Four checks in this order, and the order
matters:

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
| `GET /healthz` | liveness, unsigned |

There is no admin site, no login and no session endpoint. Identity is decided in
the store and asserted here; a second way in would be a second thing to get
wrong.

## Running it

```bash
pip install -r requirements.txt
SUBSCRIPTIONS_DEBUG=true python manage.py migrate
SUBSCRIPTIONS_DEBUG=true python manage.py runserver 8100
```

Register a store — this is an operator action, not something a store can do for
itself, because a service that registered whoever called it would have no notion
of who is allowed to call it:

```bash
python manage.py knight_store add --slug camden-coffee --store-id <uuid> --secret <shared secret>
```

## Testing

```bash
python manage.py test
```

Sixteen tests, and most of them are attacks: another store's secret, a body
altered after signing, a stale timestamp, a replay, a failed signature trying to
burn a nonce, a disabled store, a shopper reaching a staff route, and a shopper
reading another shopper's subscription by guessing its reference.
