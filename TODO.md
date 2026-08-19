# KNIGHT — Project TODO & Status

Last updated: **2026-08-19** (revision 11 — phase 3.5 store-side installer)
Authoritative docs: [`docs/README.md`](docs/README.md)

Legend: `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked / needs a decision

---

## Where the project stands

| | |
|---|---|
| **Current phase** | **Phase 3.5 — Feature registry & delivery (in progress)** |
| **Next phase** | Phase 4 — Servers, agents, monitoring |
| **Overall progress** | ~68% (both ends of delivery now exist: KNIGHT publishes, resolves and queues, and the store claims, verifies, installs and rolls back. What is missing is the packaging pipeline and the reference features to push through it) |
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
Phase 3.5  Feature registry & delivery    ███████░░░  70%   ← in progress
Phase 4    Servers, agents, monitoring    ░░░░░░░░░░   0%
Phase 5    Errors & incidents             ░░░░░░░░░░   0%
Phase 6    Frontend dashboard             █████████░  92%
Phase 7    Observability                  ░░░░░░░░░░   0%
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

## Phase 3.5 — Feature registry & delivery ← the core of revision 2

**Exit criteria:** one real Feature is implemented once, published, and
installed automatically into two different stores, upgraded, rolled back, and
uninstalled — with no manual per-store work at any point.

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
- [ ] Configuration JSON Schema validation against the manifest
- [x] Rollback orchestration incl. `ManualInterventionRequired` outcome
- [ ] Reconciliation job + `feature.drift` detection
- [x] Endpoints: install/upgrade/enable/disable/uninstall/rollback/configuration/plan, `/jobs/*`
- [x] Agent job channel: claim, report a step, report an outcome (outbound-only)
- [ ] A hosted service running the claim-expiry sweep on a timer
- [ ] SignalR: `jobProgress`, `jobCompleted`, `featureInstallationStateChanged`

### Package pipeline
- [ ] `features/` layout and a cookiecutter-style feature template
- [ ] Manifest spec implementation (`knight_manifest.yaml`)
- [ ] Build + sign + publish pipeline to the private package registry
- [x] Registry implementation chosen: object storage with KNIGHT as the index (`risks.md` §3 Q8)
- [x] Signing key custody chosen: Ed25519 behind `ISigner`, file-backed now, KMS-ready (Q9, R21)
- [x] Signer, artifact store and expiring download URLs (ECDSA P-256; .NET 10 ships no Ed25519)
- [ ] Reference Feature: one real capability, one model, one migration, a health check, tests
- [ ] A second Feature depending on the first, to exercise dependency resolution

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
- [ ] Install / upgrade / rollback / uninstall against a real store + database
- [x] Dependency resolution: diamonds, ranges, cycles, yanked versions, conflicts, downgrades
- [x] Compatibility refusal (store too old/new, wrong runtime, unreported runtime, shared hosting)
- [x] Job idempotency: a repeated step report updates in place and never downgrades a success
- [ ] Failure injection at every step; correct state and rollback outcome each time
- [ ] Irreversible-migration failure → `ManualInterventionRequired` + incident
- [x] Unsigned / tampered artifact rejected, including one signed by an untrusted key
- [x] Agent rejects unknown job types, and unknown steps
- [x] Entitlement lost → **disable**, not uninstall; data retained (store side; end-to-end pending)
- [ ] Isolation: an agent cannot claim or read another store's jobs

### Documentation
- [ ] Feature author guide (how to build, test, and publish a Feature)
- [ ] Runbook: failed installation, stuck job, manual-intervention rollback

---

## Phase 4 — Servers, agents, monitoring

- [ ] `Servers` module: registry, hosting model, environment, status
- [ ] Agent registration with one-time provisioning tokens
- [ ] Agent endpoints: handshake, heartbeat, metrics, events, **job polling and reporting**
- [ ] KNIGHT Agent implementation: telemetry + typed job execution, least privilege, no shell
- [ ] Signed agent releases and a self-update path
- [ ] `ServerMetric` ingestion with time partitioning + retention job
- [ ] Status evaluation rules and `Alert` creation
- [ ] `GET /api/v1/monitoring/overview`
- [ ] Tests: heartbeat expiry → offline, recovery, retention, agent token scope, job scoping

---

## Phase 5 — Errors, incidents, notifications

- [ ] Fingerprinting + normalisation per ADR 0013 (`fingerprintVersion` stored)
- [ ] `ErrorGroup` upsert with counters and bounded event samples
- [ ] Group lifecycle: acknowledge / resolve / ignore / regression reopen
- [ ] `Incidents` from rules and manual creation, `IncidentEvent` timeline
- [ ] Alert rules incl. `feature.install.failed`, `feature.entitled_not_installed`, `feature.drift`, `job.stuck`
- [ ] `Notifications`: channels, delivery, retry, preferences
- [ ] SignalR hub with server-side authorised subscriptions
- [ ] Tests: grouping, spike detection, incident lifecycle, delivery, isolation

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
- [ ] Edit/save write paths for every form (blocked on the API)
- [ ] Subscription change flow with live price quote (blocked on `/subscriptions/quote`)
- [ ] Live job progress over SignalR (currently one-shot fetch)
- [ ] Component and Playwright test suites

---

## Phase 7 — Observability of KNIGHT itself

- [ ] Structured JSON logging with the full correlation context
- [ ] OpenTelemetry traces across HTTP, EF Core, Redis, outbound store calls, jobs
- [ ] Self-metrics per `docs/observability.md` §3, including job/installation metrics
- [ ] `traceparent` propagation to stores and into job execution
- [ ] Central redaction helper + a test that no secret can reach a log sink or job output
- [ ] Retention jobs for metrics, health checks, error events, logs, job history

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
