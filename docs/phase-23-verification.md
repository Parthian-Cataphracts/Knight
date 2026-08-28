# Phase 23 — how it was verified, and what verifying it found

Phase 23 had one exit criterion: **an order placed in the reference store is
received by a real subscriptions service, and a merchant's request reaches that
service through the store's proxy and comes back** — both over the real delivery
path, asserted by the drill.

It was chosen because phase 22 built the whole API-driven architecture against
nothing. `subscriptions` 2.0.0 named a service at
`https://subscriptions.knight.dev` and no such thing existed; the store
registered webhooks it would never deliver and proxy routes that forwarded to a
host that did not answer. Every test passed and not one byte had crossed between
a store and a service.

The phase found **six defects**, four of which made the architecture unusable in
practice, and every one of them was invisible until two real processes had to
agree with each other.

---

## 1. What was built

| | |
|---|---|
| **The service** | `services/subscriptions/` — an ordinary Django application with its own database, its own settings and no dependency on any store |
| **Its half of the contract** | HMAC verification, skew window, replay rejection, four webhook receivers, a public route, the proxied shopper and staff APIs, `/healthz` |
| **The delivery queue** | A table, a worker, exponential retry over roughly twelve hours, then a dead letter that is kept |
| **The store publishing** | `apps/orders` announces `order.placed`, `order.paid` and `order.cancelled` through the façade |
| **`docker-compose.yml`** | PostgreSQL, Redis and the service, so one command stands the picture up |
| **Drill steps 12 and 13** | The end-to-end proof, and the gate |

### What moved, and the one thing that had to change

The domain moved essentially unchanged: the models, the state machine, the
billing clock, the ledger, the providers. What it lost was the store's database
handle and what it gained was its own.

**Every row now belongs to a store.** In 1.x each store had its own copy of these
tables, so "this store's subscriptions" was the whole table. One deployment
serving every shop makes that a data leak rather than a query, so:

- `Subscription.store`, and a reference unique **within** a store rather than
  globally — two shops both numbering from `SUB-1` is the normal case, and a
  global unique index would have made the second shop's first subscription fail
  to create;
- `SubscriptionOrder.store`, for the same reason applied to order numbers;
- every request-driven function in `services.py` takes the store as its first
  argument, and it is not optional.

Per-store configuration became a column rather than a file, read through a
context manager so that forgetting to unset it cannot bill one store under
another's provider.

---

## 2. What verifying it found

### The store signed one path and the service verified another

The proxy signed its canonical string over the path **on the store**
(`/plans`), and the service builds its own from the path it actually received
(`/api/v1/subscriptions/plans`). Every proxied request would have failed
verification with a signature that was perfectly correct about the wrong thing.

The delivery worker had the same defect in a different form: it derived the path
by slicing the URL on `/` rather than parsing it.

Both are the same mistake, and it is the one that a contract built on *both ends
deriving the canonical form independently* is most prone to: the two ends must
derive it from the same facts, and "the path" is two different facts.

### The store identified itself by the Feature's name

`X-Knight-Store` was set from `contract.slug` — the **Feature's** slug — in both
the proxy and the delivery worker. The service looked up a store by it and found
nothing, so every request was refused as `store.unknown`.

The fix is not just the value. The header is now set inside `sign()`, so a caller
cannot forget it or get it wrong; and the store identifies itself by the **store
id KNIGHT issued** rather than by a slug, because a slug is a name a merchant can
change and an id is the stable name across all three systems.

### The webhook demanded something the store cannot know

`order_placed` required a `periodSequence`. A period is the service's idea; a
store that had to know about them would be a store coupled to this Feature's
internals, which is precisely what the architecture exists to prevent.

The store now carries an **opaque external reference** and the order number, and
the service works out which period that means — the oldest paid period still
owing an order, or a line in the subscription's history when none is owing.
`Order.external_reference` is generic and the base store never interprets it,
which is the same `source_*` discipline every other outside reference in that app
follows.

### Business code reached past the façade

`apps/orders` imported `knight_integration.external` directly, and the store's own
boundary test caught it within a minute. Business code may only ask about
Features through `knight_integration.features`, so `announce()` went on the
façade. A business module that imported the event bus would be coupled to how
KNIGHT happens to deliver events today.

That test has now paid for itself twice.

### The drill's readiness probe read a 401 as "not started"

`/api/knight/health` is KNIGHT's own endpoint and answers 401 to anything
unsigned. The probe accepted only 200 or 503, so it waited its full sixty seconds
for a store that had been serving for fifty-nine of them.

### A stopped server kept serving

`StoreServer.stop()` waited for the process and returned; the socket outlives it
for a moment. The restart that followed could not bind, exited, and the **old**
process kept answering — so the drill tested a urlconf built before the Feature
was installed and reported a 404 that had nothing to do with the code under test.

That one cost about an hour, which is why `stop()` now waits for the port to go
quiet and `start()` notices a process that exited rather than trusting whatever
answers on the port.

---

## 3. Why none of this was caught before

Every one of these lives between two processes. Phase 22's tests were thorough
and all of them ran inside one: the store's tests asserted what the store
*would* send, and there was nothing on the other side to disagree.

The pattern is now three phases old and worth stating plainly. Phase 18 found
eight defects the first time the catalogue went through the real delivery path.
Phase 20 found seven the first time a second runtime claimed a real job. Phase 23
found six the first time two processes had to agree about a signature. **A
contract with one implementation is a guess.**

---

## 4. How to test it

### Everything at once

```bash
docker compose up -d postgres
```

Then, from four terminals or one script — the drill does all of it:

```bash
python tools/delivery-drill/drill.py
```

Steps 12 and 13 are this phase. Step 12 places a real order and asserts the
service recorded it; step 13 is the gate.

### By hand, if you want to watch it

```bash
cd services/subscriptions
SUBSCRIPTIONS_DEBUG=true python manage.py migrate
SUBSCRIPTIONS_DEBUG=true python manage.py runserver 8100
```

Register the store — an operator action, because a service that registered
whoever called it would have no notion of who may call it:

```bash
python manage.py knight_store add --slug camden-coffee --store-id <uuid> --secret <shared secret>
```

Set the same secret in the store's environment as
`SUBSCRIPTIONS_SERVICE_SECRET`, install the Feature, then place an order and run
the worker:

```bash
python manage.py knight_deliver
```

Expect `1 delivered, 0 retrying, 0 dead-lettered.` The service's own database is
where to check it arrived — not a log line, and not something the store showed
you:

```bash
python manage.py shell -c "from subscriptions.models import SubscriptionEvent; print(list(SubscriptionEvent.objects.values_list('reason', flat=True)))"
```

### The gate

Stop the service. Place an order. Run the worker — it reports `1 retrying`.
Start the service, bring the retry clock forward, run the worker again:

```bash
python manage.py shell -c "from django.utils import timezone; from knight_integration.external.delivery import WebhookDelivery, DeliveryState; WebhookDelivery.objects.filter(state=DeliveryState.PENDING).update(next_attempt_at=timezone.now())"
python manage.py knight_deliver
```

The event arrives. **This is the difference between a working queue and a lucky
one**, and it is the assertion the phase is gated on rather than a nice extra.

---

## 5. Test results

| Suite | Result |
|---|---|
| Backend unit | **680 passed** |
| Backend architecture | 13 passed |
| Backend integration, `REQUIRE_POSTGRES_TESTS=1` | **164 passed** |
| Reference store, all Features, `REQUIRE_FEATURE_TESTS=1` | **814 passed** (13 new) |
| Node reference store | 30 passed |
| .NET store agent | 31 passed |
| **Subscriptions service** | **17 passed** (new) |
| Delivery drill | **14 steps, all green** |

The service's seventeen are mostly attacks: another store's secret, a body
altered after signing, a stale timestamp, a replay, a failed signature trying to
burn a legitimate store's nonce space, a disabled store, a store id that is not
a UUID, a shopper reaching a staff route, and a shopper reading another
shopper's subscription by guessing its reference.

---

## 6. What is deliberately not covered

**The shared secret is set by hand.** Both ends need to agree and this phase
creates both; the drill uses a fixed string that says so in its own name. KNIGHT
issuing a per-store secret and rotating it without an outage is
[phase 24](roadmap.md), and pretending otherwise here would have been the kind of
half-measure this project keeps finding.

**The service is not deployed anywhere.** It runs in `docker compose` and in CI.
Putting it on a host is phase 27 and waits on the same hosting decision
everything else does.

**Only three of the four events are published.** `order.refunded` has a receiver
and nothing in the base store emits it, because the store has no refund flow yet.
The receiver is written and the subscription is declared, so the day it exists
the wiring is already there — but it is not exercised and is not claimed to be.

**The billing loop is not closed.** The service can bill a period and mark it
owing an order; the store command that reads that and places the order is not
written. The drill places an order directly, which proves the event path and
does not prove the loop.

**Nothing rotates the nonce table.** `forget_old_nonces` exists and is tested,
and nothing runs it on a timer. It is one cron entry and it belongs with the
rest of phase 26's operational work.
