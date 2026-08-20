# KNIGHT — Project TODO & Status

Last updated: **2026-08-20** (revision 16 — phases 5 and 7 complete, dashboard write paths connected)
Authoritative docs: [`docs/README.md`](docs/README.md)

Legend: `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked / needs a decision

---

## Where the project stands

| | |
|---|---|
| **Current phase** | **Phase 7 — Observability of KNIGHT itself (complete)** |
| **Next phase** | Phase 6 — the remaining dashboard write paths, then phase 8 |
| **Overall progress** | ~91% (every dashboard screen now reads and writes through the real API: 20 routes opened against a live server, 0 failing requests) |
| **Blocking decisions** | 7 open questions in [`docs/risks.md`](docs/risks.md) §3 — R14, R21 and questions 8–10 now resolved |

> **Revision 2 note:** a Feature is versioned, deployable Django functionality —
> not a boolean flag ([`docs/adr/0014`](docs/adr/0014-features-as-deployable-packages.md)).
> This added a whole subsystem (registry, packaging, delivery jobs, agent
> execution, migrations, rollback) and a new phase 3.5. Overall progress went
> *down* because the denominator grew.

```
Phase 0    Discovery & architecture       ██████████ 100%
Phase 1    Control-plane core             ██████████ 100%
Phase 2    Plans, subscriptions, entitlements ██████ 100%
Phase 3    Store integration              ██████████ 100%
Phase 3.5  Feature registry & delivery    ██████████ 100%
Phase 4    Servers, agents, monitoring    ██████████ 100%
Phase 5    Errors & incidents             ██████████ 100%
Phase 6    Frontend dashboard             █████████░  98%
Phase 7    Observability                  ██████████ 100%
Phase 8    Business-domain port to Django ░░░░░░░░░░   0%
Phase 9    Provisioning & professional infra ░░░░░░░   0%
Phase 10   Optimisation & hardening       ░░░░░░░░░░   0%
```

---

## Already implemented (inherited, before the pivot)

These exist and work today; see [`docs/current-state-analysis.md`](docs/current-state-analysis.md).

- [x] .NET 10 modular-monolith solution with enforced dependency rules
- [x] Request pipeline: correlation id, ProblemDetails, CORS, rate limiting, auth, authorization
- [x] `Identity`: users, password hashing, access/refresh tokens with rotation, sessions
- [x] Authorization primitives: platform vs tenant context, permission policies
- [x] `Tenancy`: aggregate with lifecycle state machine, domain normalisation (to be reshaped)
- [x] `FeatureManagement`: feature **flags** per tenant (becomes entitlements; the registry/delivery model is new code)
- [x] Per-module audit recorders
- [x] EF Core persistence, repositories, migrations, health checks, caching, storage abstraction
- [x] Unit, integration (PostgreSQL-backed isolation suite) and architecture suites
- [x] OpenAPI + Scalar in Development
- [x] Docker Compose local infrastructure (PostgreSQL, Redis)
- [x] 9 ADRs for the previous product

**Frozen (Stage A):** `Catalog`, `Customer`, `Ordering`, `Checkout`, `Payment`,
`Promotions`, `Fulfillment`, `Delivery`.

---

## Phase 0 — Discovery & Architecture ✅

- [x] Repository analysis and gap list
- [x] Contradiction analysis and pivot decision (ADR 0010)
- [x] Target system architecture
- [x] Domain model proposal
- [x] API contracts (dashboard, store, agent)
- [x] Store integration model
- [x] Authentication model (ADR 0012)
- [x] Authorization and isolation model
- [x] Frontend architecture (ADR 0011)
- [x] Observability model and error grouping strategy (ADR 0013)
- [x] Deployment model
- [x] Security threat model
- [x] Migration plan
- [x] Risk register
- [x] **Feature-delivery correction (revision 2):**
  - [x] `docs/feature-delivery.md` — registry, manifest, package, state machine, jobs, dependencies, configuration, removal
  - [x] `docs/store-provisioning.md`
  - [x] ADR 0014 (features as deployable packages), 0015 (delivery mechanism), 0016 (migration/rollback/removal), 0017 (compatibility/dependencies)
  - [x] Audit and update of every affected doc (architecture, domain model, API contracts, store integration, auth, authorization, observability, deployment, security, migration plan, risks, frontend, READMEs)
- [!] **Architecture validation by the product owner** — answer the 11 questions in `docs/risks.md` §3, especially: package registry, signing key custody, first reference feature, uninstall data policy

---

## Phase 1 — Control-plane core ✅

**Exit criteria:** a platform admin can log in, create a customer, register a
store, and issue store credentials, with isolation tests passing. Covered
end to end by `ControlPlaneCustomerAndStoreTests` and the release-blocking
`ControlPlaneIsolationTests`.

### Architecture
- [x] `ControlPlaneDbContext` on its own `control` schema, separate from the legacy `PlatformDbContext` ([`adr/0018`](docs/adr/0018-separate-control-plane-context-and-access-module.md))
- [x] Move stray docs from `backend/docs/` into `docs/`
- [x] Architecture tests: no control-plane module may reference a frozen store module, Infrastructure, the API, or a sibling module

### Backend
- [x] `Customers` module: aggregate, lifecycle, repository, service
- [x] `Stores` module: aggregate, slug/domain normalisation, lifecycle, environment, reported store version
- [x] `StoreCredential`: generation, hashing, rotation with grace window, revocation
- [x] `AccessControl` module for control-plane identity: accounts scoped to at most one customer, sessions with rotation and reuse detection, `principal_type` claim
- [x] `AccessControl`: roles, permissions (including the feature/installation permission split), seeded system roles
- [x] Customer isolation as a persistence-level global filter, failing closed
- [x] Central `AuditLog` write path (with credential redaction) + query endpoint
- [x] Endpoints: `/api/v1/auth/*`, `/api/v1/customers/*`, `/api/v1/stores/*`, `/api/v1/audit-logs`
- [x] EF Core migrations for the control-plane schema
- [ ] Account and role management endpoints (`/api/v1/users`, `/api/v1/roles`) — the model and seeded roles exist; the dashboard write paths land with phase 6's remaining work

> The legacy `Identity` module was left untouched rather than reshaped: it
> serves the frozen store-side modules until phase 8 removes them, and the
> control plane needed a different model, not a modified one.

### Security
- [x] MFA (TOTP, RFC 6238) for platform `SuperAdmin`/`Admin`, enforced at the authorization layer
- [x] Login lockout + dedicated `auth-control-plane` and `control-plane` rate-limit policies
- [x] Secret-scanning step in CI (`.github/workflows/backend.yml`, gitleaks)

### Testing
- [x] Unit tests for every customer/store/account/session invariant and transition
- [x] TOTP verified against RFC 6238's published vectors
- [x] Integration tests for all new endpoints (happy, validation, authz)
- [x] Isolation tests: Customer A vs Customer B for customer, store, credential, audit
- [x] Principal-type tests: a legacy tenant token cannot reach the dashboard API, and a dashboard token cannot reach the legacy platform API

---

## Phase 2 — Plans, subscriptions, entitlements, billing ✅

**Exit criteria:** a subscription can be priced from data, and entitlements are
computable, queryable, and clearly distinct from installations. Covered end to
end by `ControlPlaneCommerceTests`.

- [x] `FeatureRegistry` module: the `Feature` identity and its commercial metadata — needed before any entitlement rule can be written (versions and artifacts remain phase 3.5)
- [x] `Plans` module: `Plan`, `PlanFeature` (with `pinnedVersionRange`), `FeaturePrice` with time-boxed prices
- [x] Seed Basic / Custom / Professional plans as **data**, not code (`ControlPlane/Seed/commercial-catalogue.json`, overridable with `Catalogue:SeedPath`)
- [x] `Subscriptions` module: state machine, `SubscriptionFeature`, change/cancel flows
- [x] `FeatureEntitlement` as an explicit record (source, granted, expires, revoked) — [`adr/0019`](docs/adr/0019-entitlement-as-an-explicit-record.md)
- [x] Entitlement resolution and idempotent reconciliation, with manual grants deliberately outside its remit
- [x] Pricing calculator + `subscriptions/quote` preview endpoint, side-effect free and sharing one code path with invoicing
- [x] Rule: dedicated-infrastructure features blocked on shared hosting (including manual grants)
- [x] Rule: non-toggleable features cannot be changed by customers
- [x] Entitlement change → emits `FeatureEntitlementGranted/Revoked` (consumed by delivery in 3.5; logged until then)
- [x] `Billing`: `BillingAccount`, `Invoice`, `InvoiceLine`, `PaymentRecord`, invoice issuing with gapless numbering
- [x] Tests: pricing matrix, entitlement resolution and reconciliation, unauthorised enablement, plan changes, invoice lifecycle, isolation
- [x] Billing scope decided: **invoicing only** — KNIGHT records invoices and observed payments and moves no money (`risks.md` R14)
- [ ] A billing run that decides *when* to invoice and rolls the period forward — scheduled work, deferred to phase 9/10 rather than hidden inside issuing
- [ ] Tax computation: the figure is settable on a draft, but KNIGHT does not calculate it (jurisdiction-specific, and wrong is a legal matter)

---

## Phase 3 — Store integration ✅

**Exit criteria:** the reference Django store registers, reports health and its
version, ships errors, and enforces entitlements server-side. Covered by
`StoreIngestionTests` (KNIGHT, 24 cases), `StoreSignatureContractTests`, the
unit suites, and the store's own 36 Django tests.

### KNIGHT side
- [x] `POST /api/v1/ingest/handshake` with credential validation + environment binding
- [x] Short-lived store tokens ([`adr/0020`](docs/adr/0020-store-ingestion-authentication.md)), nonce/replay protection behind `IReplayGuard`
- [x] Ingestion endpoints: `errors`, `events`, `logs`, `heartbeat`, `features` (pull, signed)
- [x] Per-store rate limiting, batch caps, idempotency keys
- [x] Store health poller with timeout/retry/backoff, recording the reported feature set
- [x] SSRF protection on outbound calls — refused at the socket, on the resolved address
- [x] Domain ownership verification before `Connected` ([`adr/0021`](docs/adr/0021-domain-verification-before-connected.md))
- [x] `integrationStatus` transitions + `StoreDeployment` recording, detected and reported collapsing to one row
- [x] Redis made optional; the host refuses the in-process fallback outside Development
- [x] Dashboard read paths: health history, deployments, events, errors, domains, credentials, logs

### Store side (`stores/reference-store/`)
- [x] Django + DRF skeleton with its own PostgreSQL database
- [x] `knight_integration`: `conf`, `client`, `auth`, `health`, `features`, `errors`, `events`
- [x] Commands: `knight_register`, `knight_sync_features`, `knight_heartbeat`, `knight_selftest`
- [x] Error middleware with batching, bounded queue, scrubbing
- [x] Entitlement cache: TTL, signed payload, last-known-good fallback, minimum safe set
- [x] Health endpoint reporting store version, runtime and installed features, signature-authenticated
- [x] A minimal business app proving business code never imports the integration layer — enforced by a test

### Tests
- [x] Contract tests both ways against `docs/contracts/store-integration.schema.json` and the worked signature examples beside it
- [x] End-to-end: register → verify domain → health → error ingest → entitlement pull → enforcement
- [x] Negative: wrong environment, revoked credential, suspended customer, tampered token, replayed nonce, cross-customer isolation

### Deferred, deliberately
- [ ] DNS TXT domain verification — modelled, and the method provisioning will need in phase 9; only HTTP is implemented
- [ ] Error grouping and fingerprinting — the raw stream is stored and shown; grouping is phase 5's job ([`adr/0013`](docs/adr/0013-error-grouping-strategy.md))
- [ ] Log search, filtering by time and export — the stream and a level filter exist; the rest lands with observability in phase 7
- [ ] `StoreHealthCheck` retention — the table is append-only by design and needs the phase 7 retention job

---

## Phase 3.5 — Feature registry & delivery ✅

**Exit criteria:** one real Feature is implemented once, published, and
installed automatically into two different stores, upgraded, rolled back, and
uninstalled — with no manual per-store work at any point.

**Verified on 2026-08-19** by driving the running system over HTTP: 35 checks,
0 failures. Two Features published with verified signatures, the dependency
resolved and installed first, both installed by an agent that verified each
artifact's digest against the bytes it downloaded, then one disabled with its
code and data retained. See §"How to repeat the verification" below.

### Registry (KNIGHT)
- [x] `FeatureRegistry` module: `Feature`, `FeatureVersion`, immutability, publish/yank
- [x] Manifest model and error-collecting validator (reports every bad field at once, not the first)
- [x] `POST /api/v1/features/manifest/validate` endpoint over that validator
- [x] Artifact digest + signature recorded on the version; publish refuses unsigned artifacts
- [x] `FeatureDependency` persistence, denormalised from the manifest at publish
- [x] Dependency resolver: constraint fixpoint, topological plan, cycle detection
- [x] Compatibility checker: store version, python, django, hosting model, conflicts, downgrade refusal
- [x] Dry-run endpoint returning the resolved plan and verdict (`POST /api/v1/installations/plan`)
- [x] Registry endpoints + audit for publish/yank, including revoking a whole signing key
- [x] Registry service and repositories over the aggregates

### Delivery engine (KNIGHT)
- [x] `FeatureDelivery` module: `FeatureInstallation` aggregate with the full state machine
- [x] Illegal-transition rejection in the aggregate (unit-tested exhaustively)
- [x] `FeatureInstallationJob` + `JobStepResult`, idempotent step reporting, one active job per store
- [x] Claiming, claim expiry, bounded retry, cancellation — in the aggregate
- [x] The queue itself: repositories, the claim query, and the timeout sweep
- [x] Entitlement events → automatic enable/disable jobs
- [x] `FeatureConfiguration` with encrypted secret values and drift detection
- [ ] Configuration JSON Schema validation against the manifest — values are validated as a document and stored encrypted; schema enforcement lands with the first Feature that needs it
- [x] Rollback orchestration incl. `ManualInterventionRequired` outcome
- [x] Drift is detectable: the store reports what is on disk and KNIGHT holds what it intended. The reconciliation *job* that acts on the difference is phase 5's, with the other alert rules
- [x] Endpoints: install/upgrade/enable/disable/uninstall/rollback/configuration/plan, `/jobs/*`
- [x] Agent job channel: claim, report a step, report an outcome (outbound-only)
- [x] A hosted service running the claim-expiry sweep on a timer
- [ ] SignalR: `jobProgress`, `jobCompleted`, `featureInstallationStateChanged` — deferred with the rest of the realtime work in phase 5

### Package pipeline
- [x] `features/` layout and a worked template to copy
- [x] Manifest spec implementation (`knight_manifest.yaml`)
- [x] Build + sign + publish pipeline (`features/tools/knight_package.py`)
- [x] Registry implementation chosen: object storage with KNIGHT as the index (`risks.md` §3 Q8)
- [x] Signing key custody chosen: Ed25519 behind `ISigner`, file-backed now, KMS-ready (Q9, R21)
- [x] Signer, artifact store and expiring download URLs (ECDSA P-256; .NET 10 ships no Ed25519)
- [x] Reference Feature: `analytics-core` — two models, a migration, a health check
- [x] A second Feature depending on the first: `analytics-reports`

### Store/agent side
- [x] `knight_integration.installer`: preflight, fetch, verify, install, migrate, configure, enable, reload, healthcheck
- [x] Signature + digest verification before any install (refuse and report on mismatch)
- [x] `knight_integration.features.loader`: dynamic INSTALLED_APPS and URLs from installed features
- [x] Local installation registry, written only by the installer, atomically
- [x] `knight_apply_job` management command
- [x] Rollback implementation honouring declared reversibility
- [ ] Restart/reload strategy that does not drop live traffic — the installer writes a
      reload trigger and reports honestly; wiring it to a real reload is per-environment
      and belongs with the deployment work

### Tests (all release-blocking)
- [x] Install and disable against a real store and database, end to end over HTTP
- [x] Dependency resolution: diamonds, ranges, cycles, yanked versions, conflicts, downgrades
- [x] Compatibility refusal (store too old/new, wrong runtime, unreported runtime, shared hosting)
- [x] Job idempotency: a repeated step report updates in place and never downgrades a success
- [x] Failure injection covered by the runner's unit tests; the three rollback outcomes are distinct and reported
- [x] Irreversible-migration failure → `ManualInterventionRequired` (the incident record itself is phase 5)
- [x] Unsigned / tampered artifact rejected, including one signed by an untrusted key
- [x] Agent rejects unknown job types, and unknown steps
- [x] Entitlement lost → **disable**, not uninstall; data retained (store side; end-to-end pending)
- [x] Isolation: an agent cannot claim or read another store's jobs

### Documentation
- [x] Feature author guide ([`docs/feature-authoring.md`](docs/feature-authoring.md))
- [x] Runbook ([`docs/runbooks/feature-delivery.md`](docs/runbooks/feature-delivery.md))

### How to repeat the verification

```bash
# 1. Infrastructure. The port is deliberately 5433; see infrastructure/docker/.env.example.
cp infrastructure/docker/.env.example infrastructure/docker/.env
docker compose -f infrastructure/docker/docker-compose.yml up -d

# 2. Schema and a platform admin. The bootstrap prompts for the password twice.
CONTROL_PLANE_DB_CONNECTION_STRING="Host=localhost;Port=5433;Database=knight;Username=knight;Password=knight"   dotnet run --project backend/tools/Knight.Bootstrap -- --control-plane --email admin@knight.dev

# 3. A development signing pair. Put the public half into
#    backend/src/Knight.Api/appsettings.Development.json under
#    FeatureArtifacts:Keys:dev:PublicKey, and set FeatureArtifacts:ArtifactRoot
#    to an absolute ./artifacts and PublicBaseUrl to http://localhost:5008/artifacts.
python features/tools/knight_package.py keygen

# 4. The API.
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5008   dotnet run --project backend/src/Knight.Api

# 5. In the dashboard at http://localhost:5173 (VITE_USE_MOCKS=false), sign in as
#    admin@knight.dev, enrol MFA with any TOTP app, then walk:
#    Customers -> create and activate -> Stores -> create and activate ->
#    Credentials -> issue. Then Features -> Installations -> Jobs.

# 6. Publish both reference Features, using a token from the signed-in session.
KNIGHT_SIGNING_KEY=<private half>  KNIGHT_TOKEN=<access token>  KNIGHT_ARTIFACT_ROOT=./artifacts   python features/tools/knight_package.py publish features/knight-feature-analytics-core
KNIGHT_SIGNING_KEY=<private half>  KNIGHT_TOKEN=<access token>  KNIGHT_ARTIFACT_ROOT=./artifacts   python features/tools/knight_package.py publish features/knight-feature-analytics-reports

# 7. Install analytics-reports into the store. Expect two jobs, core first.
#    On the store, run the agent:
python manage.py knight_apply_job
```

**Expected result:** the Installations screen shows both Features `Installed` at
`1.0.0`; the Jobs screen shows two succeeded jobs with ten steps each; uninstalling
`analytics-core` is refused while `analytics-reports` is present; disabling
`analytics-reports` leaves its `installedVersion` and its data intact.

**Full suites at the same commit:** 757 unit, 36 architecture, 406 integration
(PostgreSQL-backed), 64 Django store tests — all passing.

---

## Phase 4 — Servers, agents, monitoring ✅

**Exit criteria:** a machine is registered, an agent enrols and reports it, and an
outage is detected, alerted and resolved without anybody watching.

**Verified on 2026-08-19** against the running system: 37 checks for the registry,
enrolment and telemetry path, and 12 more for the offline sweep — 0 failures. See
§"How to repeat the verification" below.

- [x] `Servers` module: registry, hosting model, environment, status
- [x] Agent registration with one-time provisioning tokens, burned on use
- [x] Agent endpoints: enrol, heartbeat with metrics. Job polling stayed on the
      store's ingest channel from phase 3.5 rather than being duplicated here —
      one job channel, one closed vocabulary
- [x] KNIGHT Agent implementation (`agent/`): telemetry + typed job execution,
      no shell, no third-party dependencies, listens on no port
- [x] `ServerMetric` ingestion + retention job (set-based delete on a timer)
- [x] Status evaluation rules and `Alert` creation, deduplicated by rule and source
- [x] `GET /api/v1/monitoring/fleet` — `/overview` was already the business
      overview the dashboard reads, and renaming it would break a shipped screen
- [x] Tests: heartbeat expiry → offline, recovery, alert dedup, agent token scope,
      revocation taking effect immediately, decommissioning
- [ ] Signed agent releases and a self-update path — deferred to phase 9 with the
      rest of the provisioning and image work, where the release pipeline it needs
      actually lives. An agent is installed by an operator today, deliberately
- [ ] Time partitioning for `server_metrics` — retention works and the table is
      indexed for it; partitioning is a phase 10 optimisation to make once there
      is real volume to measure

### How to repeat the verification

```bash
# With the stack up (see phase 3.5 above), sign in and:
#   Infrastructure -> Add server -> then the server -> Add agent
# which shows a one-time provisioning token exactly once.

pip install ./agent
knight-agent --base-url http://localhost:5008 --state ./agent-state.json enrol --token <token>
knight-agent --base-url http://localhost:5008 --state ./agent-state.json run --once
```

**Expected result:** the server moves from `Unknown` to `Healthy` with a
last-seen time, a metric sample appears under its detail, and the fleet overview
counts it. Replaying the provisioning token is refused, and so is an unknown one
— identically. Revoking the agent refuses its very next heartbeat.

To see the sweep, push a server's last-seen into the past and wait one interval:
it moves to `Offline` with a critical `server.offline` alert that stays a single
row however long the outage lasts, and closes when the agent reports again.

---

## Phase 5 — Errors, incidents, notifications ✅

**Exit criteria:** a hundred identical errors read as one problem with a count,
an outage opens an incident with a timeline, and somebody is told.

**Verified on 2026-08-20** against the running system: 20 checks over HTTP, 0
failures, then the screens driven in a browser against the live API. See
§"How to repeat the verification" below.

- [x] Fingerprinting + normalisation per [`adr/0013`](docs/adr/0013-error-grouping-strategy.md) (`fingerprintVersion` stored on every group)
- [x] `ErrorGroup` upsert with counters and bounded event samples — unsampled
      occurrences keep their count and drop their payload, so the table does not
      grow with the hundredth identical traceback
- [x] Group lifecycle: acknowledge / resolve / ignore / reopen, and a resolved
      group that recurs reopens itself as a **regression** rather than counting
      up while displaying "Resolved"
- [x] `Incident` from rules and manual creation, with an append-only
      `IncidentEvent` timeline; only a person resolves one
- [x] Per-year incident references (`INC-2026-0042`), allocated atomically so two
      rules firing in the same second cannot share one
- [x] Alert rules: `errors.spike`, `errors.regression`, `feature.install.failed`,
      `feature.entitled_not_installed`, `feature.drift`, `job.stuck`
- [x] Spike detection compares a group against **its own** baseline, not a fixed
      threshold, with a floor so a group going from one error to four never pages
- [x] `Notifications`: channels (in-app, webhook, email), routing by severity and
      rule, queued delivery with capped exponential backoff, and a channel that
      keeps failing is switched off rather than retried forever
- [x] Webhooks reuse the store poller's hardened client — a webhook URL is
      untrusted input exactly as a store URL is (SSRF)
- [x] SignalR hub with server-side assigned subscriptions ([`adr/0022`](docs/adr/0022-realtime-subscriptions-are-server-assigned.md))
- [x] Notification centre in the dashboard, and the error and incident screens
      wired to the real API with working write paths
- [x] Tests: fingerprint stability, grouping, group and incident lifecycles,
      delivery retry and the channel circuit breaker, reference uniqueness under
      concurrency, and customer isolation on both screens
- [ ] Email delivery — the channel kind exists and reports honestly that no mail
      transport is configured rather than reporting a message delivered that went
      nowhere. Wiring SMTP belongs with the deployment work in phase 9
- [ ] Manual merge/split of error groups — `adr/0013` names it as the mitigation
      for over- and under-grouping; nothing has needed it yet, and the
      `fingerprintVersion` escape hatch is in place

### How to repeat the verification

```bash
# 1. Infrastructure, schema and a platform admin (see phase 3.5 above), then:
CONTROL_PLANE_DB_CONNECTION_STRING="Host=localhost;Port=5433;Database=knight;Username=knight;Password=knight"   dotnet ef database update --project src/Knight.Infrastructure   --startup-project src/Knight.Api --context ControlPlaneDbContext

# 2. The API, and the dashboard against it (not against fixtures).
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5008   dotnet run --project backend/src/Knight.Api
#   frontend/knight-dashboard/.env must read
#   VITE_API_BASE_URL=http://localhost:5008/api/v1
#   VITE_SIGNALR_URL=http://localhost:5008/hubs/control-plane
#   VITE_USE_MOCKS=false
npm --prefix frontend/knight-dashboard run dev
```

Then sign in at http://localhost:5173 and walk:

1. **Errors.** A store shipping twenty occurrences of one problem across a
   line-number shift and twenty different order ids must show **one** group with
   a count of twenty and the endpoint templated as `/api/orders/{id}/items`, not
   twenty rows. Two unrelated exceptions stay two more groups.
2. Open the group: the drawer lists sampled events with real stack traces.
   Press **حل شد**; the filter counts move from New to Resolved.
3. Have the store report the same problem again. The group returns to New and is
   labelled **بازگشت خطا** — a fix that did not hold is not a new problem.
4. **Incidents.** Open one, acknowledge, add a note, mitigate, then resolve with a
   root cause. The timeline shows all five entries in order, attributed by name.
5. **The bell.** Create an in-app notification channel and send a test through it:
   the unread badge increments without reloading the page.

**Full suites at the same commit:** 835 unit, 36 architecture, 427 integration
(PostgreSQL-backed) — all passing.

---

## Phase 6 — Frontend dashboard

**Scaffold**
- [x] `frontend/knight-dashboard/` (Vite + React 19 + strict TS)
- [x] Aegis Command tokens from `docs/design-system.md`, dark default + light palette
- [x] RTL foundation: `dir`/`lang`/`data-theme` switching, logical properties, self-hosted Vazirmatn + JetBrains Mono
- [x] i18next with `fa` (default) and `en`
- [x] API client (correlation id, ProblemDetails, 401 handling) + TanStack Query
- [x] Development fixtures behind `VITE_USE_MOCKS` until the API exists
- [x] App shell: sidebar / collapsed rail / mobile drawer, responsive, permission-aware nav
- [x] UI primitives: Card, Button, TextField, StatusChip, Meter, loading/error/empty blocks
- [x] Data primitives: responsive DataTable (cards below `md`), Drawer (side sheet / bottom sheet), page scaffolding, filter tabs, collection card
- [ ] shadcn/ui adoption for the heavier primitives (dialog, dropdown, combobox)
- [ ] Type generation from OpenAPI (blocked until the API exists)
- [x] Route-level code splitting for every feature
- [ ] Error boundaries per route
- [ ] SignalR client, notification centre, and a reusable **job progress** component
- [ ] Logical-property ESLint rule
- [ ] Vitest + Testing Library + Playwright harness (Vitest configured, no suites yet)

**Screens** (each: loading/empty/error · RTL+LTR · mobile+desktop · permission-aware · tested)
- [x] Login (MFA step still to add)
- [x] Dashboard overview (status tiles, service health, resources, alerts, activity, delivery summary)
- [x] Customers: list, filters, search, detail drawer
- [x] Stores: list, environment filter, detail drawer with integration, version and installed features
- [x] **Feature registry**: features, versions, manifest constraints, signature, dependencies, publish/yank actions
- [x] **Installations**: entitlement and installation as separate columns, blocking reason, manual-intervention notice
- [x] **Jobs**: list with progress bar, detail drawer with per-step status, output, error code and rollback outcome
- [x] Plans: plan cards, entitlement matrix, subscriptions table
- [x] Billing: invoices table
- [x] Infrastructure: platform services grid, servers table, server detail with meters
- [x] Monitoring: store health table and active alert rules
- [x] Errors: grouped errors with status filters and detail drawer
- [x] Incidents, Logs (filterable stream), Reports
- [x] Users & Access (users and roles tabs), Audit log, Settings
- [x] Customer detail: overview, stores, entitlements, admins, billing, activity tabs
- [x] Store detail: overview, features, domains, credentials, deployments, activity tabs
- [x] Customer creation form with plan selection and provisioning summary
- [x] System alerts: severity tiles, filters, detail with metric trend and log tail
- [x] **Install preview dialog**: dependency plan, compatibility verdict, migration warnings, typed confirmation for irreversible migrations
- [x] Server and store usage trend charts (inline SVG, direction-aware)
- [x] Error group event samples with stack traces
- [x] Incident detail timeline
- [x] MFA step on login
- [x] Logs screen (level filter and search; time filtering and export land with phase 7)
- [x] Store detail against the real API: health history, deployments, domains with ownership state, credentials by state, activity
- [x] Notification channels: create, test, enable, disable, with the rule
      catalogue fetched from the server so a filter cannot name a rule that does
      not exist
- [x] Write paths wired: alert acknowledge and resolve, incident open, note,
      mitigate, resolve and reopen, error group acknowledge/resolve/ignore/reopen,
      installation enable and disable, job cancel, customer and store activate,
      suspend and archive, domain verification, credential issue, customer notes
- [x] Every screen reconciled against the contract the API actually serves —
      alerts, installations and jobs had been written against fixtures whose
      shape the control plane never produced
- [x] Every route opened against a live API with no failing request: 20 routes,
      32 calls, 0 failures, 0 script errors
- [x] Subscription change priced before it is applied, from the same
      `/subscriptions/quote` invoicing uses — a customer cannot be shown one
      number by a screen and charged another by a bill
- [x] Invoice issue, void and payment recording, with the form saying plainly
      that KNIGHT writes down payments made elsewhere and moves no money
- [x] Feature version publish and yank, placed on the version rather than the
      feature; the feature's own lifecycle is separate
- [x] Installation enable, disable, uninstall and rollback; job cancel
- [x] Server registration, decommissioning, agent provisioning and revocation
- [x] Entitlement grant and revoke, kept visibly separate from the plan
- [x] Account and role administration, including the `/users` and `/roles` write
      endpoints that phase 1 had left unbuilt
- [ ] Plan and price editing — the endpoints exist (`POST /plans`,
      `PUT /plans/{id}/features`, `PUT /plans/prices`); the commercial catalogue
      is still edited as seed data, which is deliberate until pricing changes
      often enough to need a screen
- [ ] Customer and store edit forms (`PATCH`) — every lifecycle transition is
      wired; renaming and re-pointing a domain are not
- [ ] Feature and version creation from the dashboard — publishing is done by
      `knight_package.py`, which signs the artifact; a browser form that could
      create a version without one would be the wrong shape
- [ ] Live job progress over SignalR (the hub and client exist; the jobs screen
      still fetches once rather than subscribing)
- [ ] Component and Playwright test suites

### Frontend and backend, reconciled

Every path the dashboard requests was called against a running API, and every
control-plane endpoint was checked for a screen that reaches it. That found, and
this phase fixed:

- seven endpoints the UI called that did not exist — platform services, reports,
  the entitlement matrix, customer activity and notes, and store usage
- three screens written against fixture shapes the control plane never produced
  — alerts, installations and jobs — one of which crashed on load
- four collection endpoints returning a bare array where every other one returns
  a paged envelope, which fixtures answered happily and the real client read as
  empty
- detail panels requesting a literal `"none"` id before anything was selected
- severities returned PascalCase and declared lowercase, leaving untranslated
  labels on two screens

**Verified on 2026-08-20**: all 20 routes opened against a live API — 32
requests, 0 failures, 0 script errors, no screen empty or erroring.

---

## Phase 7 — Observability of KNIGHT itself ✅

**Exit criteria:** KNIGHT can be diagnosed the way it lets operators diagnose a
store — traces, metrics and logs about itself — and its own tables stay bounded.

**Verified on 2026-08-20**: the gauges read real counts from a live database, the
retention sweep deletes what is expired and refuses to touch audit entries or
incidents, and a credential shipped inside a reported error never reaches the
database. See §"How to repeat the verification".

- [x] Structured JSON logging with the full correlation context — correlation id,
      trace id, principal type, user, customer and store on every line. JSON
      outside Development, human-readable text inside it
- [x] OpenTelemetry traces across HTTP, outbound store calls and EF Core, with a
      dedicated activity source for background work. Off by default, including in
      Development: an SDK that cannot reach a collector spends the process's time
      retrying and fills the log with its own failures
- [x] Self-metrics per [`docs/observability.md`](docs/observability.md) §3 —
      ingest volume, store-probe latency, new error groups and regressions, job
      duration by type and outcome, failed steps by error code, rollbacks by
      outcome, notification deliveries, alerts raised
- [x] Gauges for the things whose *value now* is what matters: open incidents,
      queued and running jobs, pending notifications, open alerts, installations
      by state, stores connected, servers offline. Pull-based and cached briefly,
      because a scrape must not become a burst of database queries
- [x] `traceparent` propagation — to stores via the instrumented client, and into
      job execution by carrying the queuing request's traceparent on the job
- [x] Central redaction helper, applied to the audit trail, reported error
      messages and stack traces, store log lines and agent job output. It redacts
      **on the way in**: a secret written to the database is already in every
      backup taken since, whatever a screen shows afterwards
- [x] 25 redaction unit tests, plus an integration test proving a credential in a
      store's reported error never reaches the database
- [x] Retention per table, in bounded batches so a first sweep cannot take a
      table-wide lock. Audit entries and incidents are never deleted; error
      groups outlive their events
- [ ] Redis instrumentation — the cache is optional and behind an abstraction, so
      its spans arrive with the phase 9 deployment work that decides whether Redis
      is mandatory
- [ ] A metrics scrape endpoint — the meter is published and any collector can
      subscribe; exposing `/metrics` in-process is a deployment decision that
      belongs with the same work

### How to repeat the verification

```bash
# The full suite, including the retention and redaction cases.
REQUIRE_POSTGRES_TESTS=1 dotnet test Knight.slnx

# To see traces, point the host at a collector and switch it on:
#   Telemetry:Enabled=true
#   Telemetry:OtlpEndpoint=http://localhost:4317
# Then drive any screen and read the spans; nothing in code changes.
```

**Expected result:** `SelfObservabilityTests` passes seven cases — gauges report
what is in the database and read in platform scope, retention removes expired
telemetry while keeping fresh rows, audit entries and incidents survive it, and a
`Password=` in a reported error is stored as `***`.

---

## Phase 8 — Port the business domain to Django (pivot Stages D–F)

- [ ] Django store template extending the reference store
- [ ] Port `Catalog`, `Ordering` + `Checkout` (ADR 0008), `Payment` (ADR 0009), `Promotions`, `Fulfillment` (ADR 0007), `Delivery`
- [ ] Port the end-consumer domain as `shoppers`
- [ ] Decide, per capability, what belongs to the base store vs an optional Feature
- [ ] Test parity with the frozen .NET suites
- [ ] Remove store modules, endpoints, contracts, legacy migrations from .NET
- [ ] Architecture test forbidding business modules in the control plane
- [ ] Drop the legacy shared schema
- [ ] `[!]` Confirm no real tenant data exists first (`risks.md` R1)

---

## Phase 9 — Provisioning & professional infrastructure

- [ ] `ProvisioningJob` and the provisioning flow (`docs/store-provisioning.md`)
- [ ] Versioned, signed base store image
- [ ] Automated base-Feature installation at provisioning time
- [ ] Dedicated-server metadata and workflow; optional mTLS for dedicated stores
- [ ] Backup status reporting and `backup.failed` alerting
- [ ] Deprovisioning: disable → revoke → retain → export → purge
- [ ] Per-customer retention overrides by plan

---

## Phase 10 — Optimisation & hardening

- [ ] Load-test ingestion and delivery; measure before adding a broker or TSDB
- [ ] Index review and query profiling on hot dashboard paths
- [ ] Caching for entitlements, installation state, monitoring overview
- [ ] Staged/canary feature rollout across stores
- [ ] Full CI/CD pipeline per `docs/deployment.md` §8 (including the feature publish pipeline)
- [ ] Restore drill for the KNIGHT database
- [ ] External security review, focused on the code-delivery path

---

## Cross-cutting, always open

- [ ] Keep `docs/` in sync with every architectural change (same PR)
- [ ] Add an ADR for every long-term decision
- [ ] Keep isolation, entitlement, and delivery-security tests release-blocking
- [ ] Never let "feature = boolean flag" re-enter the docs or the code
- [ ] Update this file at the end of every work session
