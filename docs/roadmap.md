# Roadmap — from here to production

**Written after phase 22.** This is the whole remaining trajectory in one place,
because the phase-by-phase log in [`TODO.md`](../TODO.md) records *what happened*
and is a bad instrument for seeing *what is left*.

There are **53 open items across 18 phases**. That number sounds worse than it
is, and the shape of it is the useful part:

| | Items | Who can close it |
|---|---|---|
| Blocked on a decision or an account only the product owner has | **11** | you |
| Real engineering, not started | **27** | me |
| Deliberate non-goals, recorded so they are not rediscovered | **15** | nobody — they stay open on purpose |

The eleven in the first row are why phases 10, 11 and 21 are not ticked, and no
amount of work here closes them. They are listed first, with the exact unblock
for each.

---

## 1. Why phases 10, 11 and 21 are open

### Phase 10 — one item, and it is not a coding task

| Item | Why it is open | Unblock |
|---|---|---|
| External security review of the code-delivery path | Nobody inside the project can review the project. The scope, priorities and briefing pack are written and waiting in [`security/external-review-scope.md`](security/external-review-scope.md) | Engage a reviewer. It is a scheduling and budget decision |

Everything else in phase 10 is done: the load test (1,882 req/s, p99 31.9ms),
the index review, the caching, the staged rollout, the CI pipeline and the
restore drill. The phase is marked `[!]` rather than `[x]` precisely so this one
item cannot be quietly forgotten — risk **R16** stays open until the report
exists and every finding has a decision recorded against it.

### Phase 11 — four items, three of them decisions

| Item | Why it is open | Unblock |
|---|---|---|
| Docker images and the deploy stages | The hosting platform is unchosen, so there is nothing to build an image *for* | Choose a platform. Now separable: deploying to a server no longer waits on it |
| An offsite copy of the nightly dumps | Where backups live is a custody decision, not a default I should pick | Say where. Then it is an afternoon |
| Running the installer against a real cloud VM with real DNS | Certificate issuance needs a resolvable domain. The container run exercised everything else | A VM and a domain for an hour |
| `install-agent.sh`, and an installer for a Django store | **This one is mine.** It is real work on other machines and it was out of scope for a phase about installing KNIGHT itself | Scheduled below as part of phase 27 |

The installer itself works: five installs across two Ubuntu servers, driven
through nginx, six defects found and fixed.

### Phase 21 — three items, and phase 22 shrank them

| Item | Status | Note |
|---|---|---|
| Phonix merged | Blocked | I have no write access to `AbolfazlTafakori/Phonix`. The branch is built and the patch is on your desktop. Grant access or apply the patch |
| Neither store driven end to end against a running KNIGHT | **Mine, and now much cheaper** | Scheduled as phase 25 |
| A .NET Feature to deliver to them | **Largely obsolete** | See below |

**Phase 22 changed the arithmetic here, and it is worth being explicit about it.**
An `external_service` Feature has no runtime, so a .NET store can take delivery
of one without a single line of .NET Feature code existing. BojanStore can
install `subscriptions` 2.0.0 the moment there is a service behind it. Building a
`dotnet` Feature is still worth doing eventually — to prove the in-process path
on that runtime — but it is no longer on the critical path to "a real customer
store has a Feature".

---

## 2. What "production-ready" means here

Stated plainly, so the phases below have something to be measured against.

A paying customer's store receives a Feature they bought; it works; it can be
upgraded and rolled back; when it breaks somebody is told; and all of that
happens on infrastructure somebody else has looked at.

Six conditions:

1. **Delivered for real.** A store that is not the reference store, running code
   we did not write, takes delivery of a Feature a customer paid for.
2. **The service half exists.** External Features have services behind them,
   events are genuinely delivered, requests are genuinely proxied.
3. **It survives failure.** A service that is down does not lose events; a job
   that hangs is noticed; a rollback restores.
4. **It is observable.** Failed deliveries, stuck jobs and proxy errors are
   visible without reading a log by hand.
5. **It is deployed** from CI to a host, with TLS, backups and a way back.
6. **It has been reviewed** by somebody outside the project, and every finding
   has a decision against it.

---

## 3. The phases

Seven phases. Each has an exit criterion that is a *demonstrable fact*, not a
list of work, and a gate that must be green before the next one starts — the
same discipline phases 18 to 22 used, because it is the only thing that has
reliably caught defects on this project.

### Phase 23 — The live service layer

**Exit:** an order placed in the reference store is received by a real
subscriptions service, and a merchant's request reaches that service through the
store's proxy and comes back — both over the real delivery path, asserted by the
drill.

This is the phase that closes the largest gap phase 22 left, and section 4 below
is its step-by-step sequence.

- The `subscriptions` service, running, with its own database
- Its half of the contract: HMAC verification, replay rejection, the four
  webhook receivers, the proxied API, `/healthz`
- The reference store's **delivery queue** — a real transport, so
  `at-least-once` stops being a word in a manifest
- The store's own code publishing `order.placed` and the other three
- `docker-compose` for the whole thing, so CI can run it
- Drill step 13: place an order, assert the service saw it; call the proxied
  route, assert it answered

**Gate:** the drill step is green in CI, **and** an event survives the service
being stopped for sixty seconds and restarted.

### Phase 24 — Secrets, identity and rotation

**Exit:** each store has its own shared secret with each service, issued by
KNIGHT and rotatable without an outage, and a store whose entitlement was
revoked cannot call the service at all.

Today the secret is one environment variable an operator sets by hand. That is
correct for one store and one service and becomes an incident at ten.

- KNIGHT issues and stores the per-(store, feature) secret; it reaches the store
  as a configuration secret through the existing path
- Overlapping validity, so rotation is not an outage
- The service verifies the store's identity assertion rather than trusting the
  header
- Revocation is immediate on the service side, not only in the store

**Gate:** rotate a live secret with a request in flight and lose nothing;
revoke an entitlement and watch the next call be refused **by the service**.

### Phase 25 — The two real stores, end to end

**Exit:** BojanStore takes delivery of a Feature from a running KNIGHT, and
serves it.

Closes the open half of phase 21. Cheap now, because it can use an external
Feature and needs no .NET Feature to exist.

- Issue BojanStore a credential against a running KNIGHT; turn the agent on
- Entitle and install `subscriptions` 2.0.0
- Drive it in a browser: the proxied route answers, the sidebar mount appears
- Phonix the same, **once there is write access or the patch is applied**

**Gate:** a Feature installed on a store whose code is not in this repository,
verified through a browser, not a test.

### Phase 26 — Operating it

**Exit:** a failed webhook delivery, a proxy 502 and a job stuck in `Running` are
each visible on a screen and each raise an alert, without anybody reading a log.

- Delivery metrics: attempted, succeeded, dead-lettered, by feature and store
- The metrics scrape endpoint phase 7 left open
- Dashboard screens for deliveries and for the dead-letter queue
- Alerting rules, and a runbook per alert

**Gate:** break a service on purpose and be *told*, before looking.

### Phase 27 — Deployment

**Exit:** KNIGHT, the reference store and one service deploy from CI to a real
host, with TLS, scheduled backups going offsite, and a rehearsed way back.

Blocked on your hosting decision for the image half; the server half is not
blocked and can go first.

- `install-agent.sh` and the Django store installer (phase 11's fourth item)
- Docker images and the deploy stages of [`deployment.md`](deployment.md) §8
- Offsite backups
- The installer against a real cloud VM with real DNS

**Gate:** a deploy, and a restore from the offsite copy onto a clean machine.

### Phase 28 — Migrating the catalogue

**Exit:** every one of the sixteen Features has a recorded decision — service, or
in-process, with the reason — and the ones that should move have moved.

Not "convert everything". The honest split is the deliverable:

- **Should be services:** anything integrating a third party, anything with a
  vendor credential, anything whose logic is identical for every store —
  `external-marketplaces`, `marketing-automation`, `ai-reports`, `subscriptions`
  (done)
- **Should stay in-process:** anything that must be inside the store's
  transaction. There is no way to be inside a transaction over HTTP, and
  `advanced-inventory` reserving stock during checkout is the clearest case
- **Genuinely arguable:** the rest, and the argument gets written down

**Gate:** the decision table exists for all sixteen, and the ones marked
"service" are delivered as services.

### Phase 29 — The production gate

**Exit:** conditions 1–6 in section 2 are all true.

- The external security review, and a decision recorded against every finding
- The restore drill against production-shaped data
- The architecture-validation questions from phase 0, answered
- A decision on the in-process path: deprecated with a date, or kept
  indefinitely as the transactional option

**Gate:** this one is yours. It is the release decision.

---

## 4. The live service layer, step by step

Phase 23 in the order the work actually goes, because this was your second
question and it deserves a precise answer rather than a phase heading.

**Step 0 — a decision I need from you, or I will assume the default.**
Where does the service's code live? I propose **this repository**, under
`services/subscriptions/`, and here is the argument: the drill has to start it
in CI, and a service in another repository means CI cannot run the end-to-end
test that is the entire point of the phase. Splitting it out later is a `git
filter-repo` and a submodule; starting split makes the verification impossible
today. **Unless you say otherwise, I will build it here.**

Then:

1. **Scaffold the service.** A Django project with its own settings, its own
   database and no dependency on any store. It is a normal web application, not
   a Feature package — nothing about it is delivered anywhere.

2. **Move the domain logic in.** `knight_feature_subscriptions` already contains
   the models, the state machine, the billing clock and the ledger. It moves
   essentially unchanged; what it loses is the store's database handle and what
   it gains is its own.

3. **Build its half of the contract.** The four webhook receivers, and — before
   any of them do anything — verification of the store's signature: reject a bad
   HMAC, reject a timestamp outside the skew window, reject a nonce already
   seen, reject a store id nobody has heard of. The store already signs
   correctly; this is the other end of a function that exists.

4. **Build the proxied API.** The routes `subscriptions/` and
   `admin/subscriptions/` forward to. Identity comes from `X-Knight-Subject` and
   `X-Knight-Identity` **only after the signature has verified** — an unsigned
   request naming a customer is an unauthenticated request naming a customer.

5. **Give the reference store a real delivery queue.** This is the piece that
   turns `at-least-once` from a word into a guarantee: a table, a worker, an
   exponential retry schedule, a dead-letter after the last attempt. The bus
   already resolves subscribers and fans out; it has nowhere to hand a delivery
   to.

6. **Make the store publish.** `apps/orders` calls `publish("order.placed", …)`
   at the four points that matter. Small, and it is what makes the whole thing
   real rather than a demonstration.

7. **Compose it.** `docker-compose.yml` with PostgreSQL, KNIGHT, the reference
   store and the subscriptions service, so one command stands up the whole
   picture and CI can too.

8. **Drill step 13.** Publish the Feature, entitle it, install it, **place an
   order**, assert the service received it, call the proxied route, assert it
   answered with what the service knows.

9. **Prove the failure mode.** Stop the service, place an order, confirm the
   delivery is queued and not lost, start the service, confirm it arrives. This
   is the assertion that distinguishes a working queue from a lucky one, and it
   is the gate for the phase.

10. **CI.** The `Delivery` workflow gains the service; the drill runs against it
    on every push, exactly as it does now for the three store runtimes.

**What this phase does not do:** deploy the service anywhere. It runs in
`docker-compose` and in CI. Putting it on a host is phase 27, and it waits on the
same hosting decision everything else does.

---

## 5. Consolidated checklist

All 53 open items, classified. Nothing here is new work invented for the
roadmap — every line traces to a phase that recorded it.

### Blocked on you (11)

| # | Item | From | Unblock |
|---|---|---|---|
| 1 | Architecture validation — the 11 questions | 0 | Answer [`risks.md`](risks.md) §3 |
| 2 | External security review | 10 | Engage a reviewer |
| 3 | Hosting platform choice → Docker images, deploy stages | 11 | Choose one |
| 4 | Offsite backup destination | 11 | Say where |
| 5 | A cloud VM with real DNS | 11 | An hour of a machine |
| 6 | Phonix write access | 21 | Grant, or apply the patch |
| 7–10 | Vendor credentials: marketing-automation, ai-reports, subscriptions, external-marketplaces | 15, 17 | Four accounts |
| 11 | Provisioning automation needs a cloud account | 9 | Same decision as #3 |

### Engineering, scheduled (27)

| Phase | Items |
|---|---|
| **23** | The service; HMAC verification; webhook receivers; proxied API; delivery queue with retry and dead-letter; the store publishing events; docker-compose; drill step 13; the failure-mode proof |
| **24** | Per-store secrets; rotation with overlap; service-side verification; immediate revocation |
| **25** | BojanStore end to end; Phonix end to end; a `dotnet` Feature (no longer critical path) |
| **26** | Delivery metrics; metrics scrape endpoint (from 7); Redis instrumentation (7); delivery + dead-letter screens; alerting and runbooks; job-progress component (6) |
| **27** | `install-agent.sh`; Django store installer; images and deploy stages; offsite backups; the cloud-VM install |
| **28** | The decision table for sixteen Features; converting the ones that should move |
| **Unscheduled but small** | Config JSON Schema validation (3.5); health poller runtime block (17); domain verification exercised (3, 17); orphan identities withdrawn (12); OAuth token refresh (17) |

### Deliberate non-goals, recorded so they are not rediscovered (15)

These stay open on purpose. Each has a reason already written in `TODO.md`:
tax computation (jurisdictional, and wrong is a legal matter); restart without
dropping traffic (needs a process manager decision); signed agent releases;
`server_metrics` partitioning (retention already works); manual error-group
merge; shadcn adoption; OpenAPI type generation; per-route error boundaries;
logical-property lint; Playwright; per-feature plan composition; feature
creation from the dashboard; fuzzy matching for `advanced-search`; the two
ledgers unreachable from the dashboard; concurrency proven rather than argued.

**Concurrency is the one in that list worth promoting.** It is recorded three
times — phases 14, 15 and 17 — as "argued, not proven", and it is the kind of
thing that is fine until it is an incident. It belongs in phase 26.

---

## 6. Dependencies and gates, in order

```
23 live service ──┬── 24 secrets ──┬── 26 operating ── 29 production gate
                  │                │                        ▲
                  └── 25 real stores┘                        │
                                                             │
27 deployment ───────────────────────────────────────────────┤
   (needs: hosting decision)                                 │
                                                             │
28 catalogue migration ──────────────────────────────────────┘
   (needs: 23, and the vendor credentials for four of them)
```

- **23 blocks everything else about services.** Until an event is genuinely
  delivered, phases 24, 26 and 28 are all building on an assumption.
- **25 does not wait for 24.** A single hand-set secret is fine for two stores;
  it is the tenth that needs rotation.
- **27 can start today** for the server half, and waits on you for the image
  half.
- **29 cannot start until the review is engaged**, which is the longest lead
  time on this list and the reason it is item #2 above rather than item #11.

---

## 7. What I need from you, shortest version

1. **Where does the service's code live?** I will assume this repository unless
   told otherwise, because CI cannot run the end-to-end test any other way.
2. **The hosting platform.** It unblocks phase 27 and item #11.
3. **Engage the security reviewer**, or say it is deferred and accept R16 stays
   open. It has the longest lead time of anything here.
4. **Phonix access**, or apply the patch yourself.
5. **Backup custody** — where the offsite copy goes.

Everything else on this list, I can do.
