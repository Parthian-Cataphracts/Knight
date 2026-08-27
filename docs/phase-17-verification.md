# Phase 17 — how it was verified, and what verifying it found

Phase 17 finished the catalogue and answered the oldest open question in
[`risks.md`](risks.md). Its exit criterion was **the last two Feature families
ship, and the catalogue is complete** — and it grew a third deliverable when the
product owner answered R26 with *yes*.

That answer came first deliberately. The TODO said the decision was cheaper
before sixteen manifests existed than after, so the manifest change landed
before either new Feature was written and both were authored in the new shape
rather than migrated into it.

---

## 1. What was built

| | |
|---|---|
| [`adr/0032`](adr/0032-a-feature-declares-its-runtime.md) | A Feature declares its `runtime:`, and the wiring is named the same way for every runtime. Closes R26 |
| **`stores/node-reference-store`** | A store that is not Django and takes delivery of a signed artifact, so `node` is a runtime rather than a name in a validator |
| **`knight-feature-node-conformance`** | The Feature it receives. Not in the commercial catalogue and not for sale |
| **`subscriptions`** | Recurring orders, a state machine that pauses and resumes, and billing that cannot charge a period twice |
| **`external-marketplaces`** | Connections, a queue with idempotency and retries, and reconciliation that reports without fixing |

The catalogue is now **sixteen sellable Features**, all with a package behind
them, and the roadmap table in [`feature-catalog.md`](feature-catalog.md) is
empty for the first time since phase 9.

---

## 2. R26, and the shape of the answer

The question had been open since phase 9: everything about Feature delivery was
runtime-neutral except the one file that decided whether a Feature could be
published at all. `ManifestReader` refused a manifest with no `django:` block, so
the project simultaneously said a Feature is deployable code and never a flag
([`adr/0014`](adr/0014-features-as-deployable-packages.md)) *and* left a
non-Django store with nothing but a flag it enforced itself.

The part worth keeping from the answer is not the discriminator. It is that
**three names are runtime-neutral and only their spelling is not**:

| Neutral name | Django | Node |
|---|---|---|
| **namespace** — what migrations are recorded under | `app_label` | `namespace` |
| **module** — what the store loads | `installed_app` | `module` |
| **mount** — the symbol serving routes, and its path | `urls.include` + `urls.prefix` | `mount.export` + `mount.prefix` |

Django's four fields turned out to be those three wearing Python clothes. So the
parsed form, the wire contract and both stores' installers all speak the neutral
names, and only the *reader* knows the spelling — along with what makes each
spelling valid, which is genuinely per-runtime: an `app_label` ends up in a
migration table and a node module ends up in an `import`.

Two consequences worth recording:

**No database migration.** The runtime wiring was already read out of the signed
manifest at delivery time rather than duplicated into columns on
`FeatureVersion`. That was luck the original author earned, and it is most of why
this fitted inside a phase.

**The wire still sends `django`.** A store is upgraded on its own schedule and a
staged rollout deliberately leaves some behind; dropping the old key would have
broken exactly the stores that had not caught up, at the moment they were being
asked to install something. Both keys go out, the reference store prefers the new
one, and the old one is marked deprecated on the wire.

### `node` is real, and where its boundary is

A name in a closed list with nothing behind it is a promise.
[`stores/node-reference-store`](../stores/node-reference-store) verifies a real
ECDSA P-256 signature over a real artifact built by the real packaging tool,
unpacks it with its own dependency-free zip reader, records the migration under
the declared namespace, writes the configuration where the Feature looks for it,
mounts the declared route and answers the health check. CI runs it on every push.

One boundary, stated rather than glossed: that store reads its job payload from a
file instead of exchanging a token and claiming work over HTTP. The transport is
identical to the Django store's, and duplicating it would have demonstrated
nothing about runtime neutrality. Everything downstream of the payload arriving
is real.

---

## 3. The two Features

### `subscriptions` — one unique index doing the work

The first Feature in the catalogue whose worst failure is not losing data. It is
charging somebody who did not owe it, and the whole package is arranged around a
single constraint: a period is **opened before it is charged**, numbered per
subscription, and unique on that number. A cron that fires twice, a webhook
delivered twice and an operator running the worker by hand during an incident all
end at the same index.

`ConcurrentBillingTests` races two threads on two connections and gets one
charge. It is worth noting how this differs from the two concurrency
demonstrations before it: removing the `select_for_update` does **not** make it
fail, because the guarantee was never in the lock. That is the general lesson —
when a worker has to be correct rather than tidy, the correctness belongs in a
constraint.

Everything else follows the rules the earlier money Features set. Attempts are an
append-only ledger with no `times_charged` counter, so "why was I charged twice
in March" is a query. A refusal is a different outcome from a failure — a failure
is the provider saying no, a refusal is us not asking — because reporting them
together has a merchant chasing a payment problem that does not exist. The
default provider is `manual`, which keeps the schedule, the periods and the
ledger while moving no money, so a store that installs this and configures
nothing has a subscription book rather than a Feature quietly billing people.

### `external-marketplaces` — everything that crosses is a row

The last Feature and the one with the most third-party surface. Its design is one
sentence: a message row is written **before** anything is attempted and kept
after it succeeds. Which buys the four properties that make an integration
supportable — "did that order reach the POS" is a query rather than an inference
from logs; a redelivery is free because the unique key is the **partner's** event
id, not ours; a failure is retried with widening gaps and then abandoned in a
state carrying what a person needs to replay it; and reconciliation compares and
reports without ever quietly fixing either side.

That last one was the judgement call. A difference between a store and a
marketplace usually means one of them is right, and which one is not something a
timer should decide: a price that differs may be a commission model, a missing
order may be one they cancelled.

The webhook endpoint is the only place in this catalogue where somebody else's
server talks to a store. It records and returns, answers **200** to a duplicate
because that is what makes a partner stop retrying, and never echoes the payload.
**Authenticating it is deliberately the store's job**: every partner signs
differently — an HMAC header here, a shared secret there, mutual TLS at the
serious end — and a Feature that invented one scheme would be wrong for all of
them.

---

## 4. What verifying it found

Five real defects. Three were found by tests, one only by a browser, and one only
by CI.

### The clock advanced past the period rather than to it

`subscriptions` set the next run to a whole interval after the period's **end**
rather than to the day after it. Every monthly subscription would have billed in
two months, for ever. Caught by the test that bills a second period.

### A period that used its last retry became unreachable

Clearing `retry_at` on the final attempt dropped the period out of the query the
retry pass makes — so it could never be marked `unpaid` and sat in `past_due`
chased by nobody. The give-up decision has to be reachable by the thing that
makes it.

### The node Feature looked for its configuration in the wrong place

It read `knight_config.json` from inside its own package directory; the installer
writes it beside, which is what `Path(__file__).parent.parent` means in every
Django Feature's `config.py`. It reported configuration version 0 with a
perfectly good file one directory up.

Worth recording *which* test caught it: the one that reads the configuration back
**through the Feature's own route**. The test that checked where the installer
wrote the file passed. Asserting on the seam from both sides is what made the
difference.

### The Feature glob assumed every Feature is a Python distribution

**Found by CI and by nothing else**, on the commit that added the node
conformance Feature. Two steps in the store workflow glob every Feature in the
repository: one `pip install`s them, and one registers them with the reference
store. Both had been correct for sixteen Django Features and neither could
survive the seventeenth not being one.

`pip install` on a directory with no `pyproject.toml` fails, so the whole job
stopped. That is now a test for the file rather than a named exception list — an
exception list goes stale the first time somebody adds a second node Feature.

The second one was the more interesting failure, because it would not have
stopped anything: `knight_install_local` would have happily registered a node
Feature into a Django store's `INSTALLED_APPS`, and the store would have failed
to start afterwards with an import error naming a package that was never Python.
That command **bypasses `preflight`**, which is where a delivered package gets
its runtime checked — so the check had to be added there too. It skips rather
than refuses, because the caller is usually that glob.

### Resuming inside a paid period charged for it twice

**Found in a browser and by nothing else.** Pausing five minutes after being
billed and resuming immediately set the clock to now, so the next period was
charged a month early — the shopper paying twice for one month they already
owned.

It is the mirror of the rule `resume()` is famous for, which is exactly why it
was easy to write: resuming into the past charges for a pause nobody used, and
resuming into an already-paid period charges twice. The suite covered the
three-month pause and never touched the five-minute one. Resume is now the later
of now and the day after the last paid period ends.

---

## 5. Publishing, again

Phase 16 recorded that the catalogue seeder publishes a Feature only when it
creates the row, so flipping `"publish": true` does nothing on a database where
the Feature already exists as Draft. That is exactly what happened to both of
these, and the documented path was exercised rather than assumed:

```
POST /api/v1/features/{id}/publish
```

Both returned 200 and the dashboard went to **29 published, 0 draft**.

One thing phase 16 did *not* record, found this time: a Feature published after
the seeder last ran has **no plan membership**, because the plan half of the seed
only runs at bootstrap. Re-running `Knight.Bootstrap` fixed it and confirmed the
seeder's plan half is genuinely additive — both Features joined Custom and
Professional, and nothing already present was disturbed.

The order on an existing deployment is therefore: **publish, then re-seed.**

---

## 6. How to test it

### Bring it up

```bash
docker compose -f infrastructure/docker/docker-compose.yml up -d
```

```bash
export CONTROL_PLANE_DB_CONNECTION_STRING="Host=127.0.0.1;Port=5433;Database=knight;Username=knight;Password=knight"
```

```bash
dotnet run --project backend/tools/Knight.Bootstrap -- --control-plane --email phase17@knight.dev
```

```bash
dotnet run --project backend/src/Knight.Api --urls http://localhost:5008
```

```bash
cd frontend/knight-dashboard && npm install && npm run dev
```

`.env.local` must have `VITE_USE_MOCKS=false`, or the screens answer from
fixtures and prove nothing.

### The dashboard

Sign in at `http://localhost:5173`. The login contract takes the six-digit code
as **`mfaCode`** in the same request as the password, not as a second call — the
field is easy to guess wrong, and guessing wrong returns `mfa_required` with no
token rather than an error.

**Features** (`/features`) must show **29 total, 29 published, 0 draft**, matching

```bash
docker exec docker-postgres-1 psql -U knight -d knight -c 'select "Status", count(*) from control.features group by 1;'
```

**Plans** (`/plans`): `subscriptions` and `external-marketplaces` both read
`— / yes / yes / — / —` across Basic, Custom, Professional, Growth, Retention.

### The store, where the Features live

```bash
cd stores/reference-store
export KNIGHT_FEATURE_ROOT=$PWD/../knight-features
pip install ../../features/knight-feature-subscriptions ../../features/knight-feature-external-marketplaces
python manage.py knight_install_local ../../features/knight-feature-*/
python manage.py migrate
python manage.py runserver 0.0.0.0:8000 --noreload
```

`--noreload`, and this is worth a sentence: the feature registry is read once at
start-up and is not a file the autoreloader watches, so a store started before a
Feature was registered keeps serving 404s for it and looks like a mounting bug.
Restart the store after installing a Feature.

Give it a subscription and two integrations:

```bash
python manage.py shell -c "from knight_feature_subscriptions import providers, services as s; s.create('sub-4471', amount='25.00', lines=[{'sku':'ESP-01','name':'Espresso beans','quantity':2,'unit_price':'12.50'}], display_name='Sam', provider=providers.MANUAL); s.activate('sub-4471'); print(s.bill('sub-4471').outcome)"
```

```bash
python manage.py shell -c "from knight_feature_external_marketplaces import adapters, services as m; m.connect('deliveroo', kind='marketplace', adapter=adapters.LOOPBACK, access_token='tok'); m.connect('books', kind='accounting', adapter=adapters.LOOPBACK, access_token='tok'); m.queue('books', kind='invoice.issued', subject_type='order', subject_id='4471'); print(m.run_flush())"
```

Then the surfaces:

```bash
curl "http://localhost:8000/subscriptions/sub-4471/"
```

Expect `"periodsBilled": 1`, `"paidToDate": "25.00"`, and a `nextRunAt` **one
month** ahead — not two, which is what the first clock bug produced.

```bash
curl "http://localhost:8000/subscriptions/configuration/"
```

`secretsPresent` is a list of names. If a value ever appears here, stop.

Pausing and resuming inside a paid period must **not** make it due:

```bash
curl -X POST "http://localhost:8000/subscriptions/sub-4471/pause/" -H "Content-Type: application/json" -d '{"actor":"sam"}'
```

```bash
curl -X POST "http://localhost:8000/subscriptions/sub-4471/resume/" -H "Content-Type: application/json" -d '{"actor":"sam"}'
```

```bash
curl "http://localhost:8000/subscriptions/due/"
```

Expect `{"due": []}`. A subscription appearing here is the fourth defect in §4
having come back.

The webhook, twice with the same event id. The first is `"duplicate": false` and
the second is **200** with `"duplicate": true` — a 409 would teach the partner's
retry logic to try harder:

```bash
curl -i -X POST "http://localhost:8000/marketplaces/webhooks/deliveroo/" -H "Content-Type: application/json" -d '{"id":"evt-9001","type":"order.placed","subjectType":"order","subjectId":"8801"}'
```

```bash
curl "http://localhost:8000/marketplaces/queue/"
```

```bash
curl "http://localhost:8000/marketplaces/books/"
```

`hasAccessToken` is a boolean. There is no endpoint in this Feature that returns a
token, and there must never be.

The store-side seams, all safe on a store with neither Feature installed:

```bash
python manage.py knight_generate_subscription_orders
```

```bash
python manage.py knight_push_orders_to_partners
```

Run both twice. Neither creates or queues anything a second time.

### The node store

```bash
cd stores/node-reference-store && npm test
```

Fourteen tests against the real artifact, the real packaging tool and a real
signature. To watch it take delivery by hand, see that store's
[README](../stores/node-reference-store/README.md).

### The suites

```bash
REQUIRE_FEATURE_TESTS=1 python manage.py test
```

```bash
cd backend && REQUIRE_POSTGRES_TESTS=1 dotnet test
```

```bash
python features/tools/knight_package.py selftest
```

---

## 7. Test results

| Suite | Result |
|---|---|
| Store, all 15 Features installed, `REQUIRE_FEATURE_TESTS=1` | **775 passed**, 0 skipped (684 before) |
| Store, no Features installed at all | **775 passed**, 555 skipped |
| Node reference store | **14 passed** |
| Backend unit | **640 passed** (626 before) |
| Backend architecture | 13 passed |
| Backend integration, `REQUIRE_POSTGRES_TESTS=1` | 155 passed |
| Dashboard | 9 passed, `tsc --noEmit` clean |

The 91 new store tests are the two Features, their seams, and the runtime check on the local installer; the 14 new backend
tests are the runtime discriminator in `ManifestReader`.

---

## 8. What is deliberately not covered

**No payment vendor is wired.** `subscriptions`' `api` provider reads its named
secret, validates it, refuses at the point the vendor call would be made, and
records the refusal as a refusal rather than a decline. Which provider, under
what agreement, in which jurisdiction, is a commercial decision — the same
position phase 15 took for `marketing-automation` and `ai-reports`, and the same
reason: a plausible-looking call to an account nobody holds is worse than an
honest refusal.

**No marketplace, POS or accounting vendor is wired either.** The `loopback`
adapter exercises the whole queue — idempotency, retries, backoff, abandonment,
reconciliation — and is named so that nobody can mistake a store running it for a
store that is connected to something.

**Webhook authentication is the store's.** Stated in the Feature's own docstring,
its manifest and here, because it is the kind of gap that looks like an oversight
until somebody writes down that it is not.

**KNIGHT does not know a store's runtime.** So a job delivering a Django package
to a node store is queued rather than refused up front; the *store* refuses it in
`preflight`, before anything is downloaded, and the outcome is a failed job and an
untouched store. Teaching KNIGHT the store's runtime so it can refuse before
queuing is worth doing and is in [`../TODO.md`](../TODO.md).

**OAuth token refresh is not automated.** `external-marketplaces` stores an
expiry and marks a connection `expired` when an adapter reports a credential
failure, which is what stops a hundred retries against a revoked token. Actually
refreshing the token needs the vendor flow that is not wired.

**Concurrency is demonstrated for stock, slots and billing.** The three claims
that can charge or oversell somebody have two-connection races behind them.
Everything else in the catalogue relies on locks and constraints that have
idempotency tests but no thread racing them, which is carried forward again.
