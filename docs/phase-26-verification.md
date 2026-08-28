# Phase 26 — how it was verified, and what verifying it found

Phase 26's exit criterion: **a failed webhook delivery, a proxy 502 and a job
stuck in `Running` are each visible on a screen and each raise an alert, without
anybody reading a log.** Its gate: **break a service on purpose and be told,
before looking.**

The architecture had grown three ways to fail quietly, and each of them is
quiet for the same reason: the store handles it correctly. A delivery that runs
out of attempts is kept as a dead letter; a service that does not answer becomes
a 502 and the shopper sees a page; a Feature with no shared secret is refused
rather than called unsigned. Nothing breaks, nothing is lost that could have
been saved, and nobody finds out.

---

## 1. What was built

| | |
|---|---|
| **The store says so** | `knight_integration/errors/operational.py` — three kinds a store reports when nothing raised an exception: `knight.delivery.dead_lettered`, `knight.service.unreachable`, `knight.service.unconfigured` |
| **Wired where they happen** | the delivery queue when it gives up, and the proxy on a 502 or a missing secret |
| **KNIGHT alerts on them** | two rules, `delivery.dead_lettered` (critical) and `service.unreachable` (warning), raised by the sweep that already runs |
| **Grouped before raising** | one alert per store, Feature and kind, however many reports arrived |
| **A way to act** | `knight_deliver --replay <id>`, because the runbook says to replay a dead letter and until now there was no way to |
| **Runbooks** | [`runbooks.md`](runbooks.md) — one entry per alert, with what to look at, what to do, and when it is safe to ignore |

`job.stuck` already existed and already alerted; what it lacked was a runbook,
which it now has.

### Why the store reports rather than KNIGHT observing

The delivery queue is the store's, and a proxied request is between the store
and somebody else's service. KNIGHT is on neither path, so the only alternative
to being told is not knowing. The reports travel on the channel stores already
have — the error ingest — because a second reporting channel would be a second
thing to be down at the moment it is needed.

They are carried as their own kinds rather than as exceptions, and the kinds are
a closed list both ends agree on by name (`StoreFailureKinds`). It is never a
prefix match: a shop's own exception must not be able to become a platform alert
by being named carefully.

---

## 2. What verifying it found

### A warning is not an incident

The first version of the test asked `/api/v1/incidents` whether an alert had
been raised, and the dead-letter test passed while the unreachable-service one
failed. An incident is opened for a critical alert; a warning is an alert and
nothing more. Asking the incidents endpoint would have made "was an alert
raised" quietly depend on how serious it was — and the screen operators actually
read is `/monitoring/alerts`, which is what the test asks now.

### The dead-letter listing had no ids

The runbook said to replay one. The listing printed a date, an event and a
Feature, and no way to name a row — and there was no replay command either. Both
were written because the runbook could not be true otherwise, which is the
argument for writing runbooks against a system rather than after it.

---

## 3. How to test it

### The gate, by hand

Queue an event to a service that is not there, exhaust its attempts, and wait.

```bash
cd stores/reference-store

# One delivery, one attempt from giving up, pointed at a dead port.
python manage.py shell -c "
from django.utils import timezone
from knight_integration.external.delivery import WebhookDelivery, MAX_ATTEMPTS
WebhookDelivery.objects.create(
    feature_slug='subscriptions', event='order.placed',
    url='http://127.0.0.1:8199/hooks/order-placed', payload={'orderNumber': 9001},
    guarantee='at-least-once', attempts=MAX_ATTEMPTS - 1, next_attempt_at=timezone.now())"

python manage.py knight_deliver
# 0 delivered, 0 retrying, 1 dead-lettered.
```

Then, without touching a log, on KNIGHT's alerts screen (or
`GET /api/v1/monitoring/alerts?openOnly=false`):

```
delivery.dead_lettered  Critical
'Drill store 923e6c49' reported delivery.dead_lettered for subscriptions once.
Gave up delivering 'order.placed' to subscriptions after 7 attempt(s)…
```

That is the gate. The store's environment needs `KNIGHT_CLIENT_ID` and
`KNIGHT_CLIENT_SECRET` for the report to have anywhere to go — a store with no
control plane reports nothing, which is correct rather than degraded.

To watch the other half, stop the subscriptions service and ask the store for a
proxied route: the store answers 502, reports `knight.service.unreachable`, and
the next sweep raises a warning.

### Replaying what was given up on

```bash
python manage.py knight_deliver --dead-letters
#   #3     2026-08-28 20:10  order.placed  -> subscriptions  after 7 attempt(s): …
python manage.py knight_deliver --replay 3
```

One at a time and never automatically: twelve hours of events arriving at once,
unannounced, is its own incident, and whether the Feature can take them is a
judgement somebody makes.

---

## 4. The numbers

| | |
|---|---|
| KNIGHT backend | **691 unit**, **13 architecture**, **168 integration** (four new, on the two rules) |
| Reference store | **848**, nothing skipped (seven new, on reporting and replay) |
| Live | the gate above, run against a real KNIGHT and a real store |

---

## 5. What is still not done

Phase 26 was scoped to its exit criterion and its gate. Everything below is on
its list and was deliberately left:

- **Delivery metrics as counters** — attempted, delivered, retried,
  dead-lettered, by Feature and store. What exists is the alerting path and the
  reports it is built on; a metrics view is the next piece.
- **A metrics scrape endpoint**, and **Redis instrumentation** — both carried
  from phase 7. The meter is published over OTLP; there is still nothing to
  scrape.
- **Dashboard screens for deliveries and the dead-letter queue**, and for the
  gift-card and loyalty ledgers. Alerts appear on the alerts screen, which is
  what the exit criterion asked for; a delivery screen of its own is not built.
- **A reusable job-progress component**, carried from phase 6.
- **`server_metrics` partitioning**, carried from phase 4.
- **Manual merge and split of error groups**, carried from phase 5.
- **Log search, filtering and export**, carried from phase 3.
- **The health poller capturing the runtime block**, carried from phase 17.
- **Concurrency proven rather than argued**, recorded three times and still an
  argument.
- **The .NET agent does not report any of this.** BojanStore's proxy returns its
  502 and says nothing to KNIGHT; only the Django reference store reports. The
  library has the same three moments to report from and they are not wired.
