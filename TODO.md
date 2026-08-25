# KNIGHT — Project TODO & Status

Last updated: **2026-08-25** (revision 24 — phase 12 done: coupons, shipping and notifications are the base store's, and advanced-promotions keeps only the sophistication)
Authoritative docs: [`docs/README.md`](docs/README.md)

Legend: `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked / needs a decision

---

## Where the project stands

| | |
|---|---|
| **Current phase** | **Phase 13 — the first Features built on the corrected boundary. Phase 12 is complete: one slug across the catalogue and the registry, coupons and shipping and notifications in the base image, `advanced-promotions` 2.0.0 carrying only the sophistication, and `delivery-zones` withdrawn** |
| **Next phase** | `reviews-ratings`, then `advanced-search` on PostgreSQL only, then `customer-segmentation` as the first real dependency test |
| **Overall progress** | **Platform ~99%, catalogue ~25%.** Two numbers on purpose: the control plane and the delivery engine are finished, and the product they exist to deliver is 4 sellable Features out of 16. The base store is now a plan a real shop can run on, which is what makes the upgrade argument honest. 597 backend tests green locally (584 unit, 13 architecture; the PostgreSQL-backed integration suite needs a cluster and runs in CI), plus **184 store tests with nothing skipped** and 9 dashboard |
| **Blocking decisions** | **Are Features publishable for a store that is not Django?** Everything except the manifest is already stack-agnostic; `ManifestReader` is the one thing that is not (R26, decision 14 in [`docs/risks.md`](docs/risks.md)). Until it is answered, a non-Django store is entitled and observed but not delivered to. Separately, the **restore drill is done** and runs in CI on every push, so the proposed release blocker is answered ([`adr/0027`](docs/adr/0027-the-restore-drill-is-the-backup-test.md)). One item remains that nobody inside the project can close: the **external security review of the code-delivery path**, scoped in [`docs/security/external-review-scope.md`](docs/security/external-review-scope.md). R16 stays open until it has happened |

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
Phase 6    Frontend dashboard             ██████████  99%
Phase 7    Observability                  ██████████ 100%
Phase 8    Business-domain port to Django ██████████ 100%
Phase 9    Provisioning & professional infra ██████████ 100%
Phase 10   Optimisation & hardening       █████████░  95%
Phase 11   Deployment & installation       ████████░░  80%
Phase 12   Catalogue alignment            ██████████ 100%
Phase 13   Delivery validation on Features ░░░░░░░░░░   0%
Phase 14   Commercial foundations         ░░░░░░░░░░   0%
Phase 15   Automation                     ░░░░░░░░░░   0%
Phase 16   Operational expansion          ░░░░░░░░░░   0%
Phase 17   Recurring revenue & integrations ░░░░░░░░░  0%
```

**Catalogue status** — 7 base capabilities plus transactional notifications in
the image; 4 sellable Features (`analytics-core`, `analytics-reports`,
`advanced-promotions` 2.0.0, `log-shipping`); 12 Draft identities waiting on a
package. [`docs/feature-catalog.md`](docs/feature-catalog.md) is the list.

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
- [x] Account and role management endpoints (`/api/v1/users`, `/api/v1/roles`) — the
      endpoints existed all along; the dashboard write paths landed with the
      editability audit. Renaming an account, replacing the roles it holds,
      creating a role and changing what one grants are all in the Access screen.
      `AccountResponse` now carries `roleIds` beside the names, because a client
      matching a role on its display name picks the wrong one the first time a
      platform role and a customer role share it

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
- [x] A billing run that decides *when* to invoice and rolls the period forward — delivered in phase 10 as `IBillingService.RunAsync` and the `BillingRunner` sweep. Prepares drafts and does **not** issue them unless `Billing:IssueAutomatically` is set: issuing consumes a gapless number and is not something a default should start doing on its own
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

### Contract audit

Every path the dashboard calls, checked against the routes the API maps, and
every response type checked against what the API returns. One screen was
fiction end to end.

- [x] **The install preview called an endpoint that has never existed.** It did
      `GET /stores/{id}/features/{id}/plan`; the API serves
      `POST /installations/plan`. The response type shared no field with
      `FeaturePlanResponse` beyond a slug and a version, and the mock implemented
      the fictional path — so the dialog worked against fixtures and 404'd
      against a real server
- [x] The plan now carries what carrying it out costs: whether each step
      migrates, whether that migration is reversible, how long it is expected to
      take, and whether the store restarts. The dashboard's irreversible-migration
      gate depends on the second of those, and had been reading an invented field
      ([`adr/0016`](docs/adr/0016-feature-migration-and-removal-policy.md))

### Editability audit

Every write the API offers, reachable from the dashboard. The audit was worth
running: three of these were not missing features but silent data loss, because
the endpoints replace a whole record and the forms sent back only part of it.

- [x] **Customer** — the edit form sent name and contact email only, so every
      rename blanked the legal name and the phone number. It edits the whole
      profile now
- [x] **Store** — placement was a field on the profile update and the form never
      sent it, so renaming a store took it off its server. It is its own
      operation, `PUT /stores/{id}/server`, with its own audit action
- [x] **Store** — the Register store button had no handler at all, and a store
      could only be created as a side effect of creating a customer
- [x] **Server** — no edit form existed, and the address was not even on the
      register form though the API has always taken it
- [x] **Server** — dedication had an endpoint and no UI, so nobody could say
      which customer a dedicated machine belonged to, or see it
- [x] **Account** — renaming and role assignment had endpoints and no UI
- [x] **Role** — creating one and changing its permissions had endpoints and no
      UI, including a permission catalogue endpoint written for a role editor
      that was never built
- [x] The `Server` type described six fields the API has never returned, so the
      infrastructure screen rendered `undefined` for load, uptime, agent version
      and store count. Load comes from the fleet overview, which was not being
      called at all

### Any-stack integration
- [x] The contract described without a framework —
      [`docs/connecting-a-store.md`](docs/connecting-a-store.md): what a store of any
      stack calls, what it must serve, the two signed strings byte for byte, and the
      rules for enforcing an entitlement when KNIGHT is unreachable.
      `store-integration.md` now says plainly that it is the *Django* implementation
      of that contract rather than the definition of it
- [x] A conformance checker an integration is finished against —
      `stores/conformance/knight_conformance.py`. `selftest` reproduces the contract's
      own signed strings and runs in CI on every push, so a checker that has drifted
      fails before it can report a confident, wrong verdict about somebody else's
      store. `check` performs a real handshake against a live deployment and asserts
      the refusals too: an unsigned health request, a signature over a different path,
      an hour-old request, a replayed handshake nonce
- [!] **Feature delivery to a non-Django store** — the wire contract, the job
      vocabulary and the step names are already runtime-neutral; the manifest is not.
      `ManifestReader` refuses a manifest with no `django:` block, so a Feature cannot
      be *published* for such a store at all. Recorded as R26 and decision 14 in
      [`docs/risks.md`](docs/risks.md); it is a product decision, not an oversight

### Deferred, deliberately
- [ ] DNS TXT domain verification — modelled, and the method provisioning will need in phase 9; only HTTP is implemented
- [x] Error grouping and fingerprinting — **delivered in phase 5** (`ErrorFingerprint`, `error_groups`, the Errors screen). Entry was stale; caught in the phase 10 audit ([`adr/0013`](docs/adr/0013-error-grouping-strategy.md))
- [ ] Log search, filtering by time and export — **still open, and phase 7 passed without it.** The stream, a store filter and a level filter exist; full-text search, a time range and export do not. Re-confirmed open in the phase 10 audit rather than left pointing at a finished phase
- [x] `StoreHealthCheck` retention — **delivered in phase 7**; `RetentionService` sweeps it alongside logs, events and error events (30 days by default). Entry was stale; caught in the phase 10 audit

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
- [x] SignalR: `jobProgress`, `jobCompleted`, `featureInstallationStateChanged` — **delivered**; all three are broadcast from `AgentJobService`, addressed to the job's customer ([`adr/0022`](docs/adr/0022-realtime-subscriptions-are-server-assigned.md)). Entry was stale; caught in the phase 10 audit

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
- [ ] Signed agent releases and a self-update path — **still open; phase 9 passed
      without it.** The signing and packaging machinery it needs now exists
      (store images, `knight_package.py`, the CI packaging job), so it is
      unblocked rather than waiting on anything. An agent is installed by an
      operator today, deliberately
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
- [x] Email delivery — **delivered in phase 9**: `SmtpEmailSender`,
      `AccountInvitationSender` and the activation-link flow. It still refuses
      honestly when no mail host is configured rather than reporting a message
      delivered that went nowhere. Entry was stale; caught in the phase 10 audit
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
- [ ] Type generation from OpenAPI — **no longer blocked**: the API and its OpenAPI document exist. Worth doing precisely because phase 10 found a hand-written contract mismatch that had silently discarded every validation message
- [x] Route-level code splitting for every feature
- [ ] Error boundaries per route
- [x] SignalR client and notification centre — `lib/realtime/connection.ts` and the bell in `AppLayout`, both exercised against the live hub during the phase 10 browser run
- [ ] A reusable **job progress** component — the events are broadcast and the screens refetch; nothing renders per-step progress yet
- [ ] Logical-property ESLint rule
- [x] Vitest + Testing Library harness — 9 screen tests run in CI (upgraded to vitest 3 in phase 10)
- [ ] Playwright — still none; the browser walk is driven by hand each phase

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
- [x] Plan creation, editing and availability; customer and store edit forms —
      all behind one shared edit drawer whose job is the part easy to skip:
      showing the server's refusal, so nobody presses save and watches nothing
- [ ] Per-feature plan composition and time-boxed prices
      (`PUT /plans/{id}/features`, `PUT /plans/prices`) — the endpoints exist and
      the catalogue is still edited as seed data, which stays deliberate until
      pricing changes often enough to be worth a screen
- [ ] Feature and version creation from the dashboard — publishing is done by
      `knight_package.py`, which signs the artifact; a browser form that could
      create a version without one would be the wrong shape
- [x] Live job progress over SignalR — the delivery engine broadcasts each step
      and outcome, and the screen follows them. Broadcasts happen after the save,
      a failing channel never costs an agent its step report, and the screen says
      whether it is live, because a live screen and a stalled one look identical
      when nothing is happening
- [x] Component tests — nine cases rendering the screens against payloads copied
      from the contracts rather than from the fixtures, which is precisely the
      gap that let three screens ship against shapes the API never produced
- [ ] Playwright end-to-end suite — the browser walk is currently a scripted
      manual pass; making it a committed suite is worth doing before the surface
      grows further

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

## Phase 8 — Port the business domain to Django (pivot Stages D–F) ✅

- [x] Django store template extending the reference store
- [x] Port `Catalog`, `Ordering` + `Checkout` (ADR 0008), `Payment` (ADR 0009), `Promotions`, `Fulfillment` (ADR 0007), `Delivery`
- [x] Port the end-consumer domain as `shoppers`
- [x] Decide, per capability, what belongs to the base store vs an optional Feature — recorded as [`adr/0024`](docs/adr/0024-base-store-versus-optional-feature.md); promotions and delivery zones ship as installable Features, everything else is base store
- [x] Test parity with the frozen .NET suites — 156 Django tests
- [x] Remove store modules, endpoints, contracts, legacy migrations from .NET
- [x] Architecture test forbidding business modules in the control plane — `ControlPlaneBoundaryTests.StoreBusinessDomains_ShouldNotExist_InTheControlPlane`
- [x] Drop the legacy shared schema — migration `DropLegacyPlatformSchema`
- [x] `[!]` ~~Confirm no real tenant data exists first~~ — confirmed 2026-08-20: the frozen modules and legacy schema hold only development and test data, so the tables may be dropped without an export path (`risks.md` R1)

### Found by running it, and fixed

Driving the real stack turned up defects the suites could not, all fixed in
this phase:

- a page reload ended the session — two concurrent restores raced, and the
  second presented a refresh token the first had already rotated, which the
  server correctly read as a replay and revoked the family for
- an expired access token signed the operator out mid-form instead of being
  renewed and the request retried
- the create-customer wizard validated its fields and navigated away without
  calling anything; it now provisions the customer, store, administrator and
  subscription, and shows the one-time password once
- feature versions, install counts, store feature counts and subscription
  totals were placeholders the API never filled — one of them rendered as NaN
- background workers wrote audit entries with no correlation id
- a report with no data rendered its absent timestamp as the epoch

### How to verify it again

1. `docker compose -f infrastructure/docker/docker-compose.yml up -d` (Postgres on 5433, Redis on 6379).
2. `CONTROL_PLANE_DB_CONNECTION_STRING="Host=localhost;Port=5433;Database=knight;Username=knight;Password=knight" dotnet ef database update --project backend/src/Knight.Infrastructure --startup-project backend/src/Knight.Api --context ControlPlaneDbContext`
3. Create the first administrator: `cd backend && printf 'a-long-enough-password\na-long-enough-password\n' | CONTROL_PLANE_DB_CONNECTION_STRING="..." dotnet run --project tools/Knight.Bootstrap -- --email you@example.test`
4. Start the API: `cd backend/src/Knight.Api && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5008 dotnet run`
5. Start the dashboard: `cd frontend/knight-dashboard && npm run dev` — it reads `.env`, which already sets `VITE_USE_MOCKS=false`.
6. Open `http://localhost:5173`, sign in with the address and password from step 3, and enrol MFA with the secret the screen shows. Every screen must render real data; none may show a red failure.
7. Create a customer at `/customers/new`. Fill every field, choose Basic, submit. Expect the one-time password screen, and a new customer whose plan column reads "پایه" rather than "بدون پلن". Activate the customer and its store — a store cannot connect while either is inactive.
8. Issue a credential from the store's اعتبارنامه‌ها tab and copy both values.
9. Run the reference store against it:
   `cd stores/reference-store && KNIGHT_CLIENT_ID=... KNIGHT_CLIENT_SECRET=... KNIGHT_STORE_ID=... KNIGHT_BASE_URL=http://localhost:5008 KNIGHT_ENVIRONMENT=Development python manage.py migrate && ... runserver 127.0.0.1:8000`
   The store's environment must be Development: a Production store refuses to reach KNIGHT over plain HTTP, on purpose.
10. Expect "Handshake accepted by KNIGHT" and "Entitlements refreshed" in the store's log, and the store on `/monitoring` reporting its version with a recent contact time.
11. `curl http://127.0.0.1:8000/boom/` and expect a new grouped error on `/errors` naming that store and `/boom` within a minute.
12. `cd backend && REQUIRE_POSTGRES_TESTS=1 dotnet test Knight.slnx` and `cd stores/reference-store && python manage.py test`.

---

## Phase 9 — Provisioning & professional infrastructure ✅

**Exit criteria:** a store can be driven from registered to Active through a
recorded run whose manual steps are represented rather than hidden, and back out
again to purged data. Verified in a browser against a live server —
[`docs/phase-9-verification.md`](docs/phase-9-verification.md).

- [x] `ProvisioningJob` and the provisioning flow (`docs/store-provisioning.md`), with manual steps modelled as manual and an operator unable to tick off anything KNIGHT checks itself ([`adr/0025`](docs/adr/0025-provisioning-is-a-job-with-manual-steps.md))
- [x] A coordinator that re-evaluates unfinished runs, because every fact a step waits for happens in another module and notifies nobody
- [x] Versioned, signed base store image, carrying the `storeVersion` Feature ranges resolve against
- [x] Automated base-Feature installation at provisioning time, through the ordinary delivery pipeline
- [x] Dedicated-server metadata: a dedicated machine records its customer, and a store may only be placed on its own customer's machine, in its own environment
- [x] Optional mTLS for dedicated and customer-managed stores, checked on the handshake **and** on every authenticated ingest call
- [x] Backup status reporting, `backup.failed` on the report and `backup.overdue` from the sweep ([`adr/0026`](docs/adr/0026-knight-records-backups-it-does-not-take-them.md))
- [x] Deprovisioning: disable → revoke → stop ingestion → retain → export → purge
- [x] Per-customer retention overrides by plan; the override wins, then the plan, then the deployment default
- [x] Publish a Feature version or a base image from the dashboard — an already-signed package is uploaded, KNIGHT computes the digest the signature is checked against, and signing stays offline in `knight_package.py`
- [x] Outbound email: a new administrator receives an activation link and sets their own password. A deployment with no mail transport falls back to the one-time password and says which happened
- [ ] Automating the manual steps — creating the machine, building the instance, wiring DNS and TLS. Deliberately out of scope for this release; each is one evaluator away once the provider integration exists

---

## Phase 10 — Optimisation & hardening

**Verification:** [`docs/phase-10-verification.md`](docs/phase-10-verification.md)
— the numbers, the before/after query plans, and the two defects the browser run
found.

- [x] Load-test ingestion and delivery; measure before adding a broker or TSDB —
      `tools/Knight.LoadTest`. **1,882 req/s, 100% accepted, p99 31.9ms** over 25
      stores. Conclusion: plain PostgreSQL and EF are enough — **no broker, no
      TSDB**
- [x] Index review and query profiling on hot dashboard paths — every index led
      with `StoreId`, so the platform-wide feeds were sequential scans. Three
      time-ordered indexes took them from 18ms/15ms/8ms to under 0.15ms, and from
      linear in the row count to logarithmic. Eight paged queries also lacked a
      unique tiebreaker and could repeat or drop a row between pages
- [x] Caching for entitlements, installation state, monitoring overview — the
      entitlement set is cached per customer with immediate eviction on any grant
      or revocation. The monitoring overview was **not** cached: it was 1 + 2N
      queries for N servers, and batching fixed the shape rather than hiding it.
      Installation state measured as not worth the invalidation it would need
- [x] Staged/canary feature rollout across stores —
      [`adr/0028`](docs/adr/0028-staged-rollouts-with-a-single-store-canary.md).
      The canary is one store, no wave starts before the last one reports, and a
      failed canary halts regardless of the threshold. This is the R16 mitigation
- [x] Full CI/CD pipeline per `docs/deployment.md` §8 — lint, build, test, secret
      scan, dependency audit, migration validation (applied twice, to prove
      idempotence), the restore drill, and Feature packaging with manifest
      validation. **Docker build/push and the deploy stages are not done**: the
      hosting platform is still unchosen, so there is nothing to build an image
      for or deploy to
- [x] **Restore drill for the KNIGHT database** — the release blocker, answered.
      It runs in CI on every push rather than on a calendar: takes a real backup,
      restores it, and compares the tables, every row count, the migration
      history, and the constraints and indexes. CI also corrupts a dump on
      purpose and asserts the restore refuses it
      ([`adr/0027`](docs/adr/0027-the-restore-drill-is-the-backup-test.md),
      [`runbooks/restore-drill.md`](docs/runbooks/restore-drill.md))
- [!] **External security review, focused on the code-delivery path** — the one
      item nobody inside the project can close, and it is not claimed as done.
      Scope, priorities and the briefing pack are ready in
      [`docs/security/external-review-scope.md`](docs/security/external-review-scope.md),
      so engaging a reviewer is now a scheduling decision. R16 stays open until
      the report exists and every finding has a decision recorded against it

### Found and fixed while verifying phase 10

- [x] The rollout canary was being **skipped** — waves came back from the
      database unordered and the aggregate dispatched wave 1 while the canary sat
      pending. Invisible in memory, so sixteen unit tests passed over it; the
      browser run caught it
- [x] The dashboard **discarded every validation message** the API sends — it
      read `validationErrors`/`code` while the API emits `errors`/`errorCode`, so
      screens showed only the boilerplate title. `api-contracts.md` §1 corrected
      to describe what is actually on the wire
- [x] A critical advisory in `vitest` and a high in `vite`, found by the new
      dependency audit and fixed by upgrading rather than exempting

---

## Phase 11 — Deployment & installation

**Exit criteria:** one command turns a fresh Ubuntu or Debian server into a
working KNIGHT — reachable over TLS, with a first administrator, migrations
applied and a nightly backup scheduled — without disturbing anything else
already running on that machine.

**Verification:** [`docs/phase-11-verification.md`](docs/phase-11-verification.md)
— five installs across two systemd Ubuntu servers, the result driven through
nginx, and the six defects that only a real second install could show.

- [x] `install.sh` — a one-command install for Ubuntu 22.04+ and Debian 12+.
      Asks everything up front, then runs unattended: packages, toolchain,
      database, Redis, build, configuration, migrations, service, nginx, TLS,
      first administrator, nightly backup
- [x] `knightctl.sh` — status, checks, logs, start/stop/restart, update, backup,
      restore, add an administrator, change the domain, set the signing key,
      show configuration, uninstall. Installed as `/usr/local/bin/knightctl`
- [x] [`docs/installation.md`](docs/installation.md) — what the installer
      creates, what it deliberately does not, and the promises it keeps to the
      other applications on the server
- [x] **A single-hostname topology.** `deployment.md` §4 describes two hosts;
      this deploys one, routing by path. One DNS record, one certificate, and no
      cross-origin request to get wrong — which matters because a CORS mistake
      is invisible to every test that is not a browser, and this project has
      already been bitten by one
- [x] The dashboard bundle carries **no hostname and no scheme**. Left unset,
      `VITE_API_BASE_URL` and `VITE_SIGNALR_URL` default to relative paths, so
      `knightctl domain` moves a deployment without rebuilding anything
- [x] Sharing a server, verified rather than asserted: only `127.0.0.1`
      listeners besides nginx, the stock nginx site still enabled and served,
      one `conf.d` file with both of its names prefixed `knight_`, one
      PostgreSQL role and one database, a dedicated Redis instance with its own
      password and a 256MB `noeviction` ceiling, and a private .NET and Node
      under `/opt/knight/toolchain` wherever the host's are too old — never a
      second toolchain in `/usr/share`
- [x] `knight-api` confined by systemd: `ProtectSystem=strict`,
      `NoNewPrivileges`, and exactly three writable directories
- [x] Nightly backup as a systemd timer — the scheduling `deployment.md` §10
      listed as still missing. `knightctl backup` starts the same unit rather
      than running the script itself, so a manual backup and a scheduled one
      cannot drift apart
- [x] Re-running the installer is safe: the token and store signing keys are
      kept (rotating either would sign out every administrator or invalidate
      every cached entitlement), and no second administrator is created

### Found and fixed while verifying phase 11

- [x] **Nothing read the reverse proxy's forwarded headers.** Every deployment
      terminates TLS at a proxy, so every request arrived from `127.0.0.1` —
      which handed the whole internet a single rate-limit bucket on sign-in and
      ingestion, and recorded the proxy's address as the client's on every login
      and audit row. `ForwardedHeaders` is now read, from named proxies only.
      The framework's defaults do not recognise a plain IPv4 loopback, which is
      exactly what Kestrel reports for a proxy on the same machine, so both
      loopback forms are named explicitly. `ReverseProxyTests` covers the
      scheme, the caller's address, and headers from an address that is not a
      known proxy being ignored
- [x] `docs/deployment.md` §5 listed configuration keys that do not exist —
      `Knight__Jwt__SigningKey`, `Knight__Environment`, `Knight__Registry__*`.
      Anyone deploying from it would have configured nothing at all. Rewritten
      against the sections the code actually binds
- [x] A machine-specific absolute path (`C:/Users/<name>/…`) was the development
      artifact root, so every other checkout wrote artifacts to a directory that
      did not exist
- [x] **Every re-install and every `knightctl update` failed after the first
      install.** `chown -R knight` puts the checkout under the service user, and
      git run as root then refuses it — "detected dubious ownership". The first
      install works, the second one aborts at the source step, and nothing shows
      it until a server has been installed twice. The exception is now granted
      per git invocation rather than written into root's global gitconfig, so it
      does not apply to any other repository on the machine
- [x] A re-install **silently dropped the artifact signing keys**. The
      environment file is rewritten from scratch, and the keys live in it under
      their own ids. A retired key still has to verify the versions it signed, so
      losing one makes already-published Feature versions unverifiable. Every
      key is now carried across, not only the active one
- [x] The installer's exit status was whatever its last statement happened to
      return. It is explicit now: zero unless the API never answered, which is
      the one thing above a warning that a provisioning system needs to see
- [x] **`knight-restore.sh` needed a privilege the application role does not
      have.** It drops and recreates the target database, and the role KNIGHT
      connects as owns one database and is not a superuser — so a real restore
      dropped the database and then could not recreate it, leaving nothing. The
      CI drill never showed it, because there the role owns the cluster. The two
      statements now run through `KNIGHT_ADMIN_PSQL` (the local superuser, where
      there is one) and the database is recreated with an explicit owner, so the
      application role can restore into it. The drill is unchanged and still
      passes
- [x] `knightctl` reported success the moment systemd returned, several seconds
      before the API was serving — so `domain`, `restart`, `signing-key`,
      `update` and `restore` all sent the operator to a 502 they would
      reasonably read as a broken deployment. They wait for the readiness probe
      now, and say so when it does not come

### Not done

- [ ] Docker images and the deploy stages of `deployment.md` §8 — still waiting
      on the hosting-platform decision, and now clearly separable from it:
      deploying to a server no longer waits on choosing a platform
- [ ] An offsite copy of the nightly dumps. The timer writes them to the same
      machine, and the installer says so rather than implying otherwise. Where
      they should go is a custody decision, not a default
- [ ] `install-agent.sh` for the servers that host stores, and an installer for
      a Django store. Both are other machines, and out of scope here
- [ ] Running the installer against a real cloud VM with real DNS. The container
      run exercised everything except certificate issuance, which needs a
      resolvable domain

---

## Phase 12 — Catalogue alignment and the base-store boundary

**Why this is first.** Everything below depends on the catalogue and the
package registry naming the same things, and on the base/optional line being
settled. Building eleven Features on top of an unsettled boundary means moving
their data later.

**Exit criteria:** a fresh deployment can be seeded, and every package in
`features/` can be published and installed against it without a manual edit;
Basic is a plan a real shop can run on.

### Done
- [x] **One slug for the catalogue and the package**
      ([`adr/0029`](docs/adr/0029-one-slug-for-the-catalogue-and-the-package.md)).
      The commercial seed named `analytics`, `loyalty`, `order-management`,
      `ai-recommendations`; the packages were `knight-feature-*`. The two sets
      did not overlap at all, so publishing any real package against a freshly
      seeded KNIGHT failed on "no feature is registered with slug". The whole
      delivery engine worked and nothing could be delivered. Manifests, seed,
      dev registry and tests now use one short slug each
- [x] **The base/optional line revised** — basic coupons and shipping are base,
      only the sophistication is sold
      ([`adr/0024`](docs/adr/0024-base-store-versus-optional-feature.md)). A
      shop that cannot issue a discount code or charge by delivery area is
      missing table stakes, and charging for them monetises a deficiency
- [x] The catalogue seeded as the whole product surface: 7 base capabilities,
      4 sellable Features, 13 Draft identities. Plans list published Features
      only — a Draft one fails `CanBeEntitled`, so listing it would put a toggle
      on the Custom screen that refuses every time it is used
- [x] [`docs/feature-catalog.md`](docs/feature-catalog.md) — the tiers, the
      catalogue, the dependency graph, and the procedure for adding a Feature

### Done, continued
- [x] **Base coupon rules moved into `apps.promotions`.** They shipped inside
      the promotions Feature. `manage.py knight_absorb_promotions` moves a
      store's rows across — idempotent, with a `--dry-run` that is genuinely
      dry — and was run against a store carrying a real campaign, a coupon and
      two redemptions
- [x] **`delivery-zones` folded into `apps.fulfillment` and withdrawn.** The
      package is deleted, its catalogue identity removed, and CI no longer
      installs it. `manage.py knight_absorb_delivery_zones` moves the zones, the
      pause switch and the store default; the Feature's `DeliverySettings`
      collapses into `FulfillmentSettings`
- [x] **`advanced-promotions` 2.0.0** carries only the sophistication: buy X get
      Y with the trigger items excluded from their own reward, whole-bundle
      pricing, per-order award caps, and an explicit stacking flag. It owns its
      own tables and never extends the base store's — a Feature may not import
      store business code, so it answers through a service taking plain basket
      lines and returning plain data
- [x] The upgrade migration **declares itself irreversible**, because it drops
      the promotion, coupon and redemption tables. Django can recreate the
      tables and cannot recreate a customer's campaigns, so claiming otherwise
      would mean a rollback that reports success and has destroyed a year of
      redemption counts. Absorb first, then upgrade — in that order, and the
      order is in the migration's own docstring
- [x] `OrderPromotion` holds. It was written to survive an uninstall and now
      also survives a *relocation*: an order priced by a rule that has since
      moved into the base store, or been deleted with the Feature, still reads
      correctly. Covered both ways in the suites
- [x] **`notifications` in the base store** — order and payment confirmation,
      cancellation, fulfilment and password reset, over Django mail with a
      console backend by default so a laptop needs no SMTP. Every send is
      recorded including the failures, because "did the customer get it" is the
      first question support asks. One notification per order and kind, enforced
      by constraint rather than by a check two concurrent checkouts both pass
- [x] Store version bumped to **2.0.0**, which is what `advanced-promotions`
      2.0.0 requires: on a 1.x store the base promotion tables do not exist and
      the upgrade would drop the only promotions that store has
- [x] Verified against a running PostgreSQL with real legacy data, not only in
      tests: [`docs/phase-12-verification.md`](docs/phase-12-verification.md).
      **184 store tests pass with nothing skipped**, up from 156

### Found by verifying it, and fixed
- [x] **Two constraint names collided** and Django refused the migration
      (`models.E032`) before it reached the database. Both sets of tables exist
      at once during the transition — that is the point of absorbing before
      upgrading — and PostgreSQL will not hold two constraints of one name. The
      base store's are namespaced now, with the reason in the model so nobody
      tidies it back
- [x] **The absorption commands crashed when run after the upgrade**, handing an
      operator an `ImportError` for a model that no longer exists. That is the
      most likely way to meet a transitional command — unsure whether it already
      ran — so both recognise the state and say so
- [x] **A zone could quote on a store that does not deliver.** Under the Feature
      the two switches lived in different tables and nothing joined them, so a
      collection-only store with leftover zones quoted delivery fees. `quote()`
      checks both and says which one refused

### Still open
- [ ] Withdraw the orphan identities (`analytics`, `loyalty`, `order-management`,
      `ai-recommendations`) on any deployment seeded from the old file. Seeding
      is additive and never deletes, so this is an API action, not an edit
- [ ] The dashboard has no screen for coupons, delivery zones or the
      notification log. They are base-store capabilities with no control-plane
      UI, which is consistent — KNIGHT is not a store's business backend — but
      the reference store's own admin does not surface them either, so today
      they are reachable only from a shell

---

## Phase 13 — Delivery-engine validation on real Features

**Why these three, in this order.** The point is not commercial value; it is to
put progressively harder scenarios through the delivery engine while production
risk stays low. Contained migrations, no external services, obvious UI changes,
easy rollback — then the first real dependency.

**Exit criteria:** each Feature installs, migrates, activates, is visible in a
browser, and rolls back cleanly on a real store. Class A migrations only
(`CreateModel`, nullable `AddField`, `AddIndex`).

- [ ] **`reviews-ratings`** — the best first Feature: customer-visible,
      commercially useful, database-backed, no external API, depends on the base
      store only. Tests package install, dependency resolution against base,
      migrations, admin and URL registration, template and static assets,
      entitlement, activation and deactivation
- [ ] **`advanced-search`** — a different category of Feature without
      operational risk. **PostgreSQL full-text only for 1.0.** No Elasticsearch,
      no OpenSearch, no external cluster: the goal is to test KNIGHT's delivery
      engine, not distributed infrastructure. A search adapter keeps the door
      open for a later provider
- [ ] **`customer-segmentation`** — the first real dependency test, and the one
      that proves a Feature can build on another Feature rather than on the base
      store. Declares `analytics-core` as an optional dependency; verify the
      resolver installs in topological order, refuses a range nothing satisfies,
      and will not uninstall `analytics-core` while this depends on it
- [ ] Verify an upgrade path, not only installs: publish `analytics-core` 1.1.0
      and upgrade a store that is on 1.0.0 with `analytics-reports` installed
      against it
- [ ] Rollback drill per Feature, including one deliberately failed migration

---

## Phase 14 — Commercial foundations

**Exit criteria:** the a-la-carte proposition is real — a Custom customer can
assemble a meaningfully better store from published Features.

- [ ] **`loyalty-rewards`** — points, tiers, earning and redemption rules, a
      loyalty ledger. Needs transactional consistency against order events, and
      a worker for expiry
- [ ] **`gift-cards`** — the first Feature carrying a financial ledger, and the
      first whose migrations are not automatically Class A. Balances spent
      across orders, redemption transactions, strong transactional consistency.
      Treat every migration touching a balance as Class C until proven otherwise
- [ ] Publish the Growth and Retention bundles as plan compositions rather than
      as new Features — bundling is a commercial act, and must not become a
      fifteenth package

---

## Phase 15 — Automation

**Exit criteria:** KNIGHT can act on a schedule on a store's behalf, with
per-customer cost bounded and auditable.

- [ ] **`marketing-automation`** — abandoned cart, welcome, post-purchase and
      win-back campaigns. Depends on `customer-segmentation`. Requires workers,
      an email provider integration, and delivery tracking. The first Feature
      needing third-party credentials, so it is also the first real test of
      named-not-valued secrets over the install channel
- [ ] **`ai-reports`** — automated interpretation of the analytics data.
      Depends on `analytics-core`. The only Feature requiring dedicated
      infrastructure, which is fixed at publication and cannot be changed after
- [ ] Usage limits and cost controls before it is sellable, not after. An AI
      Feature whose per-customer spend is unbounded is a commercial risk, not a
      technical one
- [ ] Privacy safeguards: decide and document what store data may leave the
      store for a model provider, and record it as an ADR

---

## Phase 16 — Operational expansion

**Exit criteria:** KNIGHT is credible for a merchant with real operations
behind the shop.

- [ ] **`advanced-inventory`** — stock movements, reservations, low-stock
      alerts, purchase orders, suppliers. Inventory ledger and reservation
      locking; significant indexing
- [ ] **`restaurant-operations`** — tables, kitchen states and display
      workflow, preparation times, throttling, pickup scheduling. Needs
      real-time order updates and additional order states
- [ ] **`multi-location`** — location-scoped inventory, menus, staff and order
      routing. **High migration risk and deliberately late**: it changes the
      shape of data other Features already own, which is the argument for it
      not being an early delivery-engine test

---

## Phase 17 — Recurring revenue and external integrations

**Exit criteria:** the last two Feature families ship, and the catalogue is
complete.

- [ ] **`subscriptions`** — recurring order generation, a subscription state
      machine, payment-provider integration, retry and failed-payment handling,
      pause and resume. Financially sensitive: a bug bills a real customer
- [ ] **`external-marketplaces`** — delivery marketplaces, POS and accounting
      synchronisation. Integration framework, OAuth credentials, webhooks,
      retry queues, idempotency, per-provider adapters and reconciliation.
      Deliberately last: highest complexity, most third-party surface
- [ ] Revisit R26 before this ships. If a non-Django store must receive
      Features, the manifest's `django:` block needs a `runtime:` discriminator,
      and that decision is cheaper before fourteen manifests exist than after

---

## Cross-cutting, always open

Standing rules, not unfinished work. They have no "done" state and are
deliberately left unticked forever — marking them complete would be the mistake.

- Keep `docs/` in sync with every architectural change (same commit)
- Add an ADR for every long-term decision
- Keep isolation, entitlement, and delivery-security tests release-blocking — the
  staged-rollout tests joined that set in phase 10
- Never let "feature = boolean flag" re-enter the docs or the code
- Update this file at the end of every work session
