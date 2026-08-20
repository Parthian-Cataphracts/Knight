# Phase 10 — verification

Status: **done except the external security review**, 2026-08-20. Everything
below was carried out against a running stack, not a test host.

## 1. What was built

| TODO item | Where it lives |
|---|---|
| Load-test ingestion and delivery; measure before adding a broker or TSDB | `backend/tools/Knight.LoadTest` — numbers in §3 |
| Index review and query profiling on hot dashboard paths | `PlatformWideTimeIndexes` migration; before/after in §4 |
| Caching for entitlements, installation state, monitoring overview | `CachingCustomerEntitlementReader`; the overview was fixed by batching, not caching (§5) |
| Staged/canary feature rollout across stores | `FeatureRollout`, `/api/v1/rollouts`, [`adr/0028`](adr/0028-staged-rollouts-with-a-single-store-canary.md) |
| Full CI/CD pipeline per `deployment.md` §8 | `.github/workflows/` — what is and is not there in §6 |
| **Restore drill for the KNIGHT database** (release blocker) | `infrastructure/scripts/`, [`adr/0027`](adr/0027-the-restore-drill-is-the-backup-test.md), [`runbooks/restore-drill.md`](runbooks/restore-drill.md) |
| External security review of the code-delivery path | **outstanding** — scoped in [`security/external-review-scope.md`](security/external-review-scope.md) |

## 2. How to run it

```bash
# Infrastructure.
docker compose -f infrastructure/docker/docker-compose.yml up -d

# Schema and seed data, the way a deploy does it.
cd backend
CONTROL_PLANE_DB_CONNECTION_STRING="Host=127.0.0.1;Port=5433;Database=knight;Username=knight;Password=knight" \
  dotnet run --project tools/Knight.Bootstrap -- --migrate-only

# First administrator (password typed in, never an argument).
CONTROL_PLANE_DB_CONNECTION_STRING="..." dotnet run --project tools/Knight.Bootstrap -- --email you@example.com

# API.
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Knight.Api --urls http://localhost:5008

# Dashboard, against the real API (VITE_USE_MOCKS=false in .env.local).
cd ../frontend/knight-dashboard && npm run dev -- --port 3000 --strictPort
```

### The restore drill

```bash
PGHOST=localhost PGPORT=5433 PGUSER=knight PGPASSWORD=knight \
KNIGHT_DB=knight KNIGHT_DRILL_DB=knight_drill \
infrastructure/scripts/restore-drill.sh
```

On a machine without PostgreSQL client tools, run it inside the image — the exact
command is in [`runbooks/restore-drill.md`](runbooks/restore-drill.md) §5.
**Expected:** every check `PASS` and exit code 0.

### The load test

```bash
cd backend
CONTROL_PLANE_DB_CONNECTION_STRING="..." \
  dotnet run --project tools/Knight.LoadTest -- seed --stores 25

# Raise the per-store ingest limit first, or the run measures the rate limiter.
RateLimiting__IngestPermitLimit=100000000 \
  ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Knight.Api --urls http://localhost:5008

dotnet run --project tools/Knight.LoadTest -- \
  run --base-url http://localhost:5008 --duration 60 --concurrency 32
```

`seed` writes live store secrets to `artifacts/load-test-fixtures.json`, which is
gitignored. Delete the fixture customer when finished.

## 3. Load test — the measurement

25 stores, 32 workers, 60 seconds, on the development machine, with the ingest
rate limit raised so the write path rather than the limiter is under test:

```
Requests         112,975
Throughput       1,882 req/s
Accepted (2xx)   112,975 (100.0%)

Latency (ms)   min 5.3   p50 16.3   p90 23.8   p99 31.9   max 137.3

By endpoint    heartbeat 45,333   events 33,690   logs 22,707   errors 11,245
```

**Conclusion: no broker and no time-series database.** The phase asked to measure
before adding either, and plain PostgreSQL with EF sustains roughly 1,900 ingest
requests a second on a laptop with a p99 under 32ms. A broker would add an
operational component and a delivery-guarantee problem to solve a bottleneck that
is not there.

Two things the run found on the way:

- At the configured limit of 600 requests per store per minute, a 25-store run
  was 100% rate-limited. That is the limiter working, and it is why the tool
  counts 429s separately: a run that was mostly throttled has measured the
  limiter and not the write path.
- Malformed ingest payloads returned **500** rather than 400. The harness was at
  fault for sending them, but the response was wrong either way; the missing
  `environment` field is now sent and the endpoints were re-run clean.

## 4. Index review — before and after

The load run left 238k log entries and 180k events behind, which is enough for
the planner to tell the truth. Profiling then found one real gap: every index on
`store_log_entries`, `store_events` and `store_error_events` led with `StoreId`.
That serves a store's own detail page and cannot serve the platform-wide feed a
staff user opens first, so those listings were sequential scans plus a top-N sort
over the whole table.

| Query | Before | After |
|---|---|---|
| Platform-wide logs, newest first | 17.99 ms (parallel seq scan, 238k rows) | 0.06 ms (index scan) |
| Platform-wide events | 15.27 ms (parallel seq scan, 180k rows) | 0.13 ms |
| Platform-wide error events | 7.97 ms (seq scan, 20k rows) | 0.13 ms |

The milliseconds at this size are not the point. The scans were linear in the row
count and the index scans are not.

The per-store paths were already correctly indexed and were left alone. A
separate defect turned up in the same pass: eight paged queries ordered by a
non-unique column alone, so a row could appear on two consecutive pages or on
neither. Every paged query in the control plane now ends with a unique
tiebreaker.

## 5. Caching

- **Entitlements** are cached per customer behind a 30-second TTL, as a
  decorator, and evicted the moment a grant or revocation happens. Worth caching
  because every store polls `/ingest/features` on a timer and the set changes
  only when a subscription does. The cached value is the entitlement *set*, not
  the signed response — caching the response would freeze its `issuedAt` and
  `staleAfter`, and a store that trusts a signed set offline deserves timestamps
  that mean what they say.
- **The monitoring overview** was not cached. It was issuing 1 + 2N queries for N
  servers, and N is the fleet — the number the page exists to grow with. Caching
  would have hidden the shape; the repositories gained batch reads instead and
  the overview is now three queries whatever the fleet size.
- **Installation state** is read per store on screens that already filter by
  store, is served by an existing composite index, and changes on every job.
  Nothing measured suggested a cache would pay for the invalidation it would
  need, so none was added.

## 6. CI/CD — what exists and what does not

Against [`deployment.md`](deployment.md) §8:

| Stage | Status |
|---|---|
| lint | `dotnet format --verify-no-changes` |
| build | backend, dashboard (`tsc --noEmit` + `vite build`), reference store |
| test | unit, architecture, PostgreSQL-backed integration, vitest, Django |
| security | gitleaks secret scan, `dotnet list package --vulnerable`, `npm audit` |
| migration validation | applied to a fresh database, then applied **again** to prove idempotence |
| restore drill | runs on every push, including a corrupted dump that must be refused |
| feature publish | packages built and manifests validated |
| docker build + push | **not done** — no Dockerfile, and the platform is not chosen |
| deploy staging → smoke → production | **not done** — there is no environment to deploy to |

The last two are honestly out of reach rather than skipped: the hosting platform
is still undecided, so there is nothing to build an image for or deploy to.

The dependency audit found real problems on its first run — a critical advisory
in vitest and a high in vite, both in the build and test toolchain rather than in
anything the dashboard ships. Fixed by upgrading to vite 7 and vitest 3.

Note also that `npm run lint` is declared in `package.json` but eslint is not
installed and there is no configuration, so the script cannot run. It is not
wired into CI for that reason. Left as-is rather than half-done.

## 7. Browser verification

Signed in as a real SuperAdmin with TOTP, dashboard on port 3000 against the API
on 5008 with `VITE_USE_MOCKS=false`.

Walked: the rollout screen end to end, plus Logs, Errors, Monitoring,
Installations & Jobs, Infrastructure, Incidents, Alerts, Stores and Customers —
the screens the index, paging and caching changes touch. All render live data; no
red failures; the only console errors were SignalR retries during the deliberate
API restart, and they cleared on reconnect.

The rollout walk-through, with `knight-feature-analytics-core` installed on four
active stores at 0.9.0 and 1.0.0 published:

1. **Plan** — four stores split into `Canary — one store` (1), `Wave 1` (1),
   `Wave 2` (2), state `Planned`, nothing sent anywhere.
2. **Start** — the canary wave alone moves to `Dispatched`; waves 1 and 2 stay
   `Pending`.
3. **Halt** — state `Halted`, the reason shown on the card, and the actions
   become Resume and Cancel.

### Two defects this found, both invisible to the test suite

**The canary was being skipped.** A rollout's waves come back from the database
in whatever order the query produced — not insertion order, and not stable — and
the aggregate read that collection directly for every sequencing decision. On the
first browser run, `Start` left the canary `Pending` and dispatched **wave 1**,
which defeats the entire mitigation. It is invisible in memory because a freshly
planned rollout is already in order, which is why sixteen unit tests passed over
it. Waves are now always read in ordinal order; the regression test reverses the
backing collection to stand in for the reload, and reverting the fix makes it
fail.

**Every validation message in the dashboard was being discarded.** The problem
document carries `errorCode`, `correlationId` and `errors`; the client read
`code`, `requestId` and `validationErrors` — names the documentation described
and nothing ever emitted. So screens showed `One or more validation errors
occurred.` and nothing more. The operator now reads `No store has this Feature
installed on a different version, so there is nothing to roll out.` The API's
names won because the store client already depends on them, and
[`api-contracts.md`](api-contracts.md) §1 was corrected to describe what is
actually on the wire.

### A flaky test, found by running the suite five times

`AnInAppChannelReceivesAnAlertAndTheDeliveryIsRecorded` failed about one run in
three with `expected 1, actual 3`. The channel it creates has
`minimumSeverity: Info` and no rule filter, so it is subscribed to everything and
goes on receiving real alerts raised by the rest of the suite for as long as it
exists. The suite was working correctly; the assertion was wrong about what it
owned. It now asserts that its delivery is present and that every delivery
returned belongs to its channel, rather than that it is the only one. Five
consecutive full runs green afterwards.

## 8. Test results

```
Knight.UnitTests          581 passed, 0 failed, 0 skipped
Knight.ArchitectureTests   13 passed, 0 failed, 0 skipped
Knight.IntegrationTests   138 passed, 0 failed, 0 skipped   (REQUIRE_POSTGRES_TESTS=1)
knight-dashboard            9 passed (vitest)
reference-store           156 passed (REQUIRE_FEATURE_TESTS=1, both Features registered)
```

## 9. What is still open

- **The external security review of the delivery path.** Nobody inside the
  project can close it. Scope, priorities and what the reviewer needs are in
  [`security/external-review-scope.md`](security/external-review-scope.md), and
  R16 in [`risks.md`](risks.md) stays open until the report exists and every
  finding has a decision against it.
- **Docker images and deploy stages**, which need a hosting platform to be chosen
  first.
- **Nightly backup scheduling and the offsite copy.** The drill proves the dump
  and the restore path; the transport is deployment configuration
  ([`deployment.md`](deployment.md) §10).
- **eslint**, which is referenced by a `package.json` script that cannot run.
