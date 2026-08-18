# KNIGHT — Project TODO & Status

Last updated: **2026-08-18** (revision 2 — feature-delivery correction)
Authoritative docs: [`docs/README.md`](docs/README.md)

Legend: `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked / needs a decision

---

## Where the project stands

| | |
|---|---|
| **Current phase** | **Phase 0 — Discovery & Architecture (complete, awaiting validation)** |
| **Next phase** | Phase 1 — Pivot Stage A/B: control-plane core |
| **Overall progress** | ~12% (analysis + architecture done, including the feature-delivery correction; reusable backend infrastructure exists; control-plane domain, feature registry/delivery, store template, agent, and frontend are all greenfield) |
| **Blocking decisions** | 11 open questions in [`docs/risks.md`](docs/risks.md) §3 |

> **Revision 2 note:** a Feature is versioned, deployable Django functionality —
> not a boolean flag ([`docs/adr/0014`](docs/adr/0014-features-as-deployable-packages.md)).
> This added a whole subsystem (registry, packaging, delivery jobs, agent
> execution, migrations, rollback) and a new phase 3.5. Overall progress went
> *down* because the denominator grew.

```
Phase 0    Discovery & architecture       ██████████ 100%
Phase 1    Control-plane core             ░░░░░░░░░░   0%
Phase 2    Plans, subscriptions, entitlements ░░░░░░   0%
Phase 3    Store integration              ░░░░░░░░░░   0%
Phase 3.5  Feature registry & delivery    ░░░░░░░░░░   0%   ← new in revision 2
Phase 4    Servers, agents, monitoring    ░░░░░░░░░░   0%
Phase 5    Errors & incidents             ░░░░░░░░░░   0%
Phase 6    Frontend dashboard             ░░░░░░░░░░   0%
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
- [x] 97 test files: unit, integration (PostgreSQL-backed isolation suite), architecture
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

## Phase 1 — Control-plane core

**Exit criteria:** a platform admin can log in, create a customer, register a
store, and issue store credentials, with isolation tests passing.

### Architecture
- [ ] Create `Knight.ControlPlane` DbContext separate from the legacy `PlatformDbContext`
- [ ] Move stray docs from `backend/docs/` into `docs/`
- [ ] Extend architecture tests: no control-plane module may reference a frozen store module

### Backend
- [ ] `Customers` module: aggregate, lifecycle, repository, service
- [ ] `Stores` module: aggregate, slug/domain normalisation, lifecycle, environment, reported store version
- [ ] `StoreCredential`: generation, hashing, rotation with grace window, revocation
- [ ] Reshape `Identity` for `customerId` scoping and the `principal_type` claim
- [ ] `AccessControl`: roles, permissions (including the feature/installation permission split), seeded system roles
- [ ] Customer isolation as a persistence-level global filter
- [ ] Central `AuditLog` write path + query endpoint
- [ ] Endpoints: `/api/v1/auth/*`, `/api/v1/customers/*`, `/api/v1/stores/*`, `/api/v1/audit-logs`
- [ ] EF Core migrations for the control-plane schema

### Security
- [ ] MFA (TOTP) for platform `SuperAdmin`/`Admin`
- [ ] Login lockout + `auth` rate-limit policy tuning
- [ ] Secret-scanning step in CI

### Testing
- [ ] Unit tests for every customer/store invariant and transition
- [ ] Integration tests for all new endpoints (happy, validation, authz)
- [ ] Isolation tests: Customer A vs Customer B for customer, store, credential, audit
- [ ] Principal-type tests: user/store/agent token cross-access rejected

---

## Phase 2 — Plans, subscriptions, entitlements, billing

**Exit criteria:** a subscription can be priced from data, and entitlements are
computable, queryable, and clearly distinct from installations.

- [ ] `Plans` module: `Plan`, `PlanFeature` (with `pinnedVersionRange`), `FeaturePrice`
- [ ] Seed Basic / Custom / Professional plans as **data**, not code
- [ ] `Subscriptions` module: state machine, `SubscriptionFeature`, change/cancel flows
- [ ] `FeatureEntitlement` as an explicit record (source, granted, expires, revoked)
- [ ] Entitlement resolution service (customer → store → feature map)
- [ ] Pricing calculator + `subscriptions/quote` preview endpoint
- [ ] Rule: dedicated-infrastructure features blocked on shared hosting
- [ ] Rule: non-toggleable features cannot be changed by customers
- [ ] Entitlement change → emits `FeatureEntitlementGranted/Revoked` (consumed by delivery in 3.5)
- [ ] `Billing`: `BillingAccount`, `Invoice`, `InvoiceLine`, `PaymentRecord`, invoice issuing
- [ ] Tests: pricing matrix, entitlement resolution, unauthorised enablement, mid-period plan changes
- [ ] `[!]` Decide billing scope: invoicing only vs payment processing (`risks.md` R14)

---

## Phase 3 — Store integration

**Exit criteria:** the reference Django store registers, reports health and its
version, ships errors, and enforces entitlements server-side.

### KNIGHT side
- [ ] `POST /api/v1/ingest/handshake` with credential validation + environment binding
- [ ] Short-lived store tokens (ADR 0012), Redis-backed nonce/replay protection
- [ ] Ingestion endpoints: `errors`, `events`, `heartbeat`, `features` (pull)
- [ ] Per-store rate limiting, batch caps, idempotency keys
- [ ] Store health poller with timeout/retry/backoff, recording reported feature set
- [ ] SSRF protection on outbound calls
- [ ] Domain ownership verification before `Connected`
- [ ] `integrationStatus` transitions + `StoreDeployment` recording

### Store side (`stores/reference-store/`)
- [ ] Django + DRF skeleton with its own PostgreSQL and Redis
- [ ] `knight_integration`: `conf`, `client`, `auth`, `health`, `features`, `errors`, `events`
- [ ] Commands: `knight_register`, `knight_sync_features`, `knight_selftest`
- [ ] Error middleware with batching, bounded queue, scrubbing
- [ ] Entitlement cache: TTL, signed payload, last-known-good fallback
- [ ] Health endpoint reporting store version, runtime, and installed features
- [ ] A minimal business app proving business code never imports the integration layer

### Tests
- [ ] Contract tests both ways against a shared schema file
- [ ] End-to-end: register → health → error ingest → entitlement change → enforcement
- [ ] Negative: wrong environment, revoked credential, expired token, foreign `storeId`, replay

---

## Phase 3.5 — Feature registry & delivery ← the core of revision 2

**Exit criteria:** one real Feature is implemented once, published, and
installed automatically into two different stores, upgraded, rolled back, and
uninstalled — with no manual per-store work at any point.

### Registry (KNIGHT)
- [ ] `FeatureRegistry` module: `Feature`, `FeatureVersion`, immutability, publish/yank
- [ ] Manifest JSON Schema + validator; `POST /api/v1/features/manifest/validate`
- [ ] Artifact digest + signature recorded on the version; publish refuses unsigned artifacts
- [ ] `FeatureDependency` / `FeatureCompatibility` persistence
- [ ] Dependency resolver: transitive graph, topological plan, cycle detection at publish
- [ ] Compatibility checker: store version, python, django, hosting model, conflicts
- [ ] Dry-run endpoint returning the resolved plan and verdict
- [ ] Registry endpoints + audit for publish/yank

### Delivery engine (KNIGHT)
- [ ] `FeatureDelivery` module: `FeatureInstallation` aggregate with the full state machine
- [ ] Illegal-transition rejection in the aggregate (unit-tested exhaustively)
- [ ] `FeatureInstallationJob` + `JobStepResult`, idempotency, one active job per store
- [ ] Job queue, claiming, timeouts, bounded retry with backoff, cancellation
- [ ] Entitlement events → automatic install/disable jobs
- [ ] `FeatureConfiguration` with schema validation and encrypted secret values
- [ ] Rollback orchestration incl. `ManualInterventionRequired` outcome
- [ ] Reconciliation job + `feature.drift` detection
- [ ] Endpoints: install/upgrade/enable/disable/uninstall/rollback/configuration/plan, `/jobs/*`
- [ ] SignalR: `jobProgress`, `jobCompleted`, `featureInstallationStateChanged`

### Package pipeline
- [ ] `features/` layout and a cookiecutter-style feature template
- [ ] Manifest spec implementation (`knight_manifest.yaml`)
- [ ] Build + sign + publish pipeline to the private package registry
- [ ] `[!]` Choose the registry implementation (`risks.md` §3 Q8)
- [ ] `[!]` Define signing key custody and rotation (Q9)
- [ ] Reference Feature: one real capability, one model, one migration, a health check, tests
- [ ] A second Feature depending on the first, to exercise dependency resolution

### Store/agent side
- [ ] `knight_integration.installer`: preflight, fetch, verify, install, migrate, configure, enable, reload, healthcheck
- [ ] Signature + digest verification before any install (refuse and report on mismatch)
- [ ] `knight_integration.features.loader`: dynamic INSTALLED_APPS/URLs/settings from installed features
- [ ] Local installation registry, written only by the installer
- [ ] `knight_apply_job` management command
- [ ] Rollback implementation honouring declared reversibility
- [ ] Restart/reload strategy that does not drop live traffic

### Tests (all release-blocking)
- [ ] Install / upgrade / rollback / uninstall against a real store + database
- [ ] Dependency resolution: diamonds, ranges, cycles, yanked versions, conflicts
- [ ] Compatibility refusal (store too old/new, wrong runtime, shared hosting)
- [ ] Job idempotency: re-running a step never double-applies
- [ ] Failure injection at every step; correct state and rollback outcome each time
- [ ] Irreversible-migration failure → `ManualInterventionRequired` + incident
- [ ] Unsigned / tampered artifact rejected
- [ ] Agent rejects unknown job types
- [ ] Entitlement lost → **disable**, not uninstall; data retained
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
- [ ] `frontend/knight-dashboard/` (Vite + React + strict TS)
- [ ] Tailwind + shadcn/ui, theme tokens, light/dark
- [ ] RTL foundation: `dir` switching, logical-property lint rule, self-hosted Vazirmatn
- [ ] i18next with `fa` (default) and `en`
- [ ] API client + TanStack Query + type generation from OpenAPI
- [ ] App shell: sidebar/rail/drawer, responsive layouts, error boundaries
- [ ] SignalR client, notification centre, and a reusable **job progress** component
- [ ] Vitest + Testing Library + Playwright harness

**Screens** (each: loading/empty/error · RTL+LTR · mobile+desktop · permission-aware · tested)
- [ ] Login (+ MFA)
- [ ] Dashboard overview (status tiles, charts, incidents, failed installations)
- [ ] Customers: list, detail, lifecycle actions
- [ ] Stores: list, detail, register, credentials, health, deployments
- [ ] **Store → Features tab**: entitlement vs installation, version, health, actions
- [ ] **Feature registry**: features, versions, manifest view, publish/yank with impact preview
- [ ] **Install preview dialog**: dependency plan, compatibility verdict, migration warnings
- [ ] **Jobs**: list, detail with live step progress, retry/cancel, failure + rollback outcome
- [ ] Plans & plan-feature matrix
- [ ] Subscriptions: create/change, feature selection with live price quote
- [ ] Billing: invoices, detail, record payment
- [ ] Servers: list, detail, metric charts, agents
- [ ] Monitoring, Errors, Incidents, Logs, Reports
- [ ] Users, Roles, Permissions, Audit logs, Settings

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
