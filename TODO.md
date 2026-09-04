# KNIGHT — Project TODO & Status

Last updated: **2026-09-04** (revision 47 — Phase 31 P3: **tenant data export** (GET /me/export + portal button, acceptance-tested) done; push-telemetry flagged cross-repo. Earlier P0–P2: KMS signer, ADR 0036, PgBouncer, IaC, billing outbox, agent hardening. The remaining Phase-31 items are cross-repo or infra-decision-gated. See Phase 31 and docs/hardening-backlog.md)
Authoritative docs: [`docs/README.md`](docs/README.md)

Legend: `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked / needs a decision

---

## Where the project stands

| | |
|---|---|
| **Next major track** | **Self-service SaaS — phases A and B done, C next.** The product owner has redirected KNIGHT from the agency model to self-service: merchants register publicly, pay KNIGHT, and get an automatically provisioned, entitled store with no operator step. The full plan (grounded in the existing modules it reuses) is [`docs/self-service-saas-plan.md`](docs/self-service-saas-plan.md); the decision that overrules "no public registration" for the customer principal is [`docs/adr/0035`](docs/adr/0035-pivot-to-self-service-saas-registration.md). Work is sequenced A–G in `Phase 30` below. |
| **Current phase** | **Phase 29 — the production gate. One item was buildable and is built; the rest is yours.** Domain verification has had two methods and one implementation since phase 3; the DNS TXT half now exists, so a store with no HTTP surface yet can prove it owns its domain. The other four items are the security review, eleven answers, a production database and a release call — none of them code. [`docs/phase-29-verification.md`](docs/phase-29-verification.md) |
| **Previous phase** | **Phase 28 — migrating the catalogue. The table is written; the three moves are not made.** Every one of the sixteen Features now has a recorded decision and a reason — four should be services, one of them is, eight stay in-process on the transaction argument, three are arguable and held together by `analytics-core`. [`docs/feature-architecture-decisions.md`](docs/feature-architecture-decisions.md), [`docs/phase-28-verification.md`](docs/phase-28-verification.md) |
| **Previous phase** | **Phase 27 — deployment. The unblocked half is done; the gate needs a host.** There are now three installers — control plane, agent, Django store — the store's unit reloads without dropping what is in flight, and the nightly dumps have a verified way off the machine. What is left needs the hosting, domain and backup-custody decisions, which are the product owner's. [`docs/phase-27-verification.md`](docs/phase-27-verification.md) |
| **Previous phase** | **Phase 26 — operating it. The gate is passed.** A delivery that gives up and a service that does not answer are now reported by the store, grouped, and raised as alerts on the screen operators already read — and every alert KNIGHT can raise has a runbook. Verified live: a delivery pointed at a dead port produced a critical alert naming the store and the Feature, with nobody reading a log. [`docs/phase-26-verification.md`](docs/phase-26-verification.md) |
| **Previous phase** | **Phase 25 — the two real stores, end to end. Complete for BojanStore.** It was connected to KNIGHT from its own admin panel — no redeploy — took delivery of `subscriptions` 2.1.0, and serves it: `GET /api/features/subscribe/` on that shop answers with the service's own reply. Phonix is carried forward, since there is no write access to it from here. [`docs/phase-25-verification.md`](docs/phase-25-verification.md) |
| **Previous phase** | **Phase 24 — secrets, identity and rotation. Complete.** A store's shared secret is a row with a lifetime, issued by KNIGHT and rotated with an overlap, and withdrawing an entitlement is refused by the **service** rather than only by the store. [`docs/phase-24-verification.md`](docs/phase-24-verification.md) says how it was checked |
| **Next phase** | **None that this repository can start.** What is left across phases 25, 27, 28 and 29 is: Phonix write access, a host and a domain, a backup-custody decision, three service conversions, an engaged security reviewer, and the eleven answers in [`docs/risks.md`](docs/risks.md) §3. The decisions are listed in [`docs/roadmap.md`](docs/roadmap.md) §7 |
| **Overall progress** | **Platform ~99%, catalogue 100%.** Two numbers on purpose, and the second one has arrived: the control plane and the delivery engine were finished in phase 15, and the product they exist to deliver is now **16 sellable Features, all with a package behind them**, in 5 plans. **891 backend tests green** (704 unit, 13 architecture, 174 PostgreSQL-backed integration), plus **848 store tests with nothing skipped**, 14 node-store tests, **57 subscriptions-service tests**, and 9 dashboard — and, since phase 19, **the delivery drill itself**, which is the only thing here that runs the path a customer travels |
| **Blocking decisions** | R26 is **answered**: a Feature is publishable for any of three runtimes, and since phase 22 an `external_service` Feature needs no runtime at all. What is left is five decisions only the product owner can make, listed in [`docs/roadmap.md`](docs/roadmap.md) §7 — where the service code lives, the hosting platform, engaging the security reviewer (longest lead time, R16 stays open until it happens), Phonix access, and backup custody |

## Phase 30 — Self-service SaaS

Full plan: [`docs/self-service-saas-plan.md`](docs/self-service-saas-plan.md) ·
decision: [`docs/adr/0035`](docs/adr/0035-pivot-to-self-service-saas-registration.md).
Most of the back end is reused; the work is the front doors, a separate
`knight_billing`, the automatic payment→provisioning wire, and a customer portal.

- [x] **A — domain foundation.** `Subscription.Pending` + provider fields +
  activate-from-pending; `Plan.IsPubliclyPurchasable`; the `PlatformBilling`
  module (`PlatformBillingTransaction`, `CheckoutSession`) with persistence and a
  migration; `ProvisioningJob` failure classification + attempt count. 18 unit
  tests; migration `SelfServiceSaaSFoundations`.
- [x] **B — public auth.** `POST /auth/register`, `/verify-email`,
  `/resend-verification`, rate-limited and anonymous. New `Onboarding` module
  (customer + owner account in one unit of work), `ControlPlaneUser.EmailVerified`
  with its own verification-token flow, a verification-email port distinct from
  the invitation one. No account-existence oracle; an account cannot sign in
  until verified. 5 unit + 5 integration tests; migration
  `SelfServiceRegistration`. *(the customer stays `Prospect` until payment in C;
  role grants beyond `CustomerOwner`, and the customer portal, are still ahead.)*
- [x] **C — billing.** public `GET /plans`; CUSTOM selection validated against
  what the plan offers; `POST /billing/checkout` with an authoritative,
  server-computed price; a payment-provider abstraction with a simulated
  provider standing in for the unchosen gateway; the provider webhook
  (signature, idempotency, replay) as the only activation path; subscription
  activation + entitlement generation via `ReconcileAsync`. Stable error codes
  carried through the exception middleware. 11 unit tests. *(Transitive
  dependency resolution across selected features is deferred to the delivery
  engine at install time, which already resolves and installs dependencies.)*
- [x] **D — provisioning automation.** The webhook→job wire creates the store
  and starts provisioning on a confirmed payment (idempotently, and it activates
  the Prospect customer). An `IInfrastructureAdapter` produces the facts the
  fact-based provisioning engine waits on — a machine with an enrolled agent, a
  credential, a verified domain, a completed handshake, a healthy report — with a
  `SimulatedInfrastructureAdapter` standing in for the unchosen cloud and a
  `SimulatedInfrastructureWorker` driving it out of band (off unless
  `Provisioning:SimulateInfrastructure=true`). The provisioning engine itself is
  **unchanged**: it still only observes facts; the adapter creates them.
- [x] **E — feature delivery integration.** Desired state resolves from
  entitlements through the **existing** `BaseFeatureInstaller` and delivery
  engine — no second installer. Proven end to end by the acceptance test: the
  purchased feature is installed by the simulated agent and the store reaches
  Active. The **acceptance test** (docs/self-service-saas-plan.md §13) is green:
  register → verify → checkout(CUSTOM) → webhook → activate → entitlements →
  store → simulated infra → feature install → **READY**, with no operator step.
- [ ] **E — feature delivery integration.** desired state from entitlements →
  existing delivery engine; dependencies; verify + report. No second installer.
- [x] **F — customer portal.** A separate, role-gated route tree from the
  operations dashboard (`features/portal`, `RoleLayout`): public sign-up and
  email verification, a plan catalogue with a CUSTOM add-on selector and a
  server-priced checkout, a portal home showing the subscription and the store
  with live provisioning progress, a store page with the friendly step timeline,
  and cancel-at-period-end. Backend: the customer `/me` API and public
  `/catalog/plans`. **Driven end to end in a browser** against the live API —
  signup → verify → login → plan → checkout → simulated payment → store Ready →
  cancel — which found and fixed three defects the type-checker and the backend
  acceptance test could not. [`docs/phase-30F-verification.md`](docs/phase-30F-verification.md).
- [x] **G — operations.** A new **Provisioning** operations screen
  (`features/provisioning`, nav under Operations, gated on `store.view`): every
  provisioning/deprovisioning run, filterable by state, with a per-step timeline
  and Retry/Resume/Cancel for an operator with `store.provision`. Reads the
  existing `/api/v1/provisioning` endpoints — no new backend. The rest of G
  already existed: entitlement grant/revoke (customer detail), suspend/restore
  (customer lifecycle), agent monitoring (infrastructure), audit (audit log).
  **Browser-verified** against the live API. [`docs/phase-30G-verification.md`](docs/phase-30G-verification.md).

Blocked on two product-owner decisions before the automation is *real* (not the
shape): the **payment provider** (drives the `knight_billing` adapter + webhook
signature) and the **hosting platform** (drives the real infrastructure adapter).
Simulated adapters run the whole journey — and its acceptance test — locally.

A first real payment adapter now exists: **`StripePaymentProvider`** behind
`IPlatformPaymentProvider`, with Stripe's real webhook-signature scheme
(unit-tested) and hosted-checkout creation. It is off unless
`PlatformBilling:Stripe:SecretKey` is set; the simulated provider stays the
default. Choosing Stripe (or adding another adapter) and supplying live keys is
the decision that remains.

### Post-Phase-30 hardening

The remaining work is production-hardening, most of it raised by an external
architecture review, tracked honestly (including where the code already
anticipates the risk) in [`docs/hardening-backlog.md`](docs/hardening-backlog.md).
The headline items: **P0** a KMS/HSM-backed artifact signer (the `IFeatureArtifactSigner`
seam already exists) and an ADR-level comparison of the runtime-install vs.
immutable-image delivery model; **P1** replace the Bash installers with IaC behind
`IInfrastructureAdapter`, and PgBouncer; **P2** finish secret rotation, agent
least-privilege, and a formal billing outbox; **P3** tenant export and push-based
telemetry. Kept as-is per the review: the modular monolith, per-store isolation,
architecture tests and ADRs.

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
Phase 13   Delivery validation on Features ██████████ 100%
Phase 14   Commercial foundations         ██████████ 100%
Phase 15   Automation                     ██████████ 100%
Phase 16   Operational expansion          ██████████ 100%
Phase 17   Recurring revenue & integrations ██████████ 100%
Phase 18   The catalogue through delivery  ██████████ 100%
Phase 19   The delivery drill runs itself  ██████████ 100%
Phase 20   Second runtime, and the refusals ██████████ 100%
Phase 21   A third runtime, and two real stores ███████░░░  70%
Phase 22   Features as services              ██████████ 100%
Phase 23   The live service layer          ██████████ 100%
Phase 24   Secrets and rotation             ██████████ 100%
Phase 25   The two real stores              ███████░░░  70%
Phase 26   Operating it                     ██████░░░░  60%
Phase 27   Deployment                       █████░░░░░  50%
Phase 28   Migrating the catalogue          ████░░░░░░  40%
Phase 29   The production gate              ██░░░░░░░░  20%
Phase 30   Self-service SaaS                ██████████ 100%  (A–G built; real provider + host are product-owner calls)
Phase 31   Production hardening             ███████░░░  70%  (P0/P1/P2 + tenant export done or authored; secret-rotation & push-telemetry are cross-repo)
```

**Catalogue status** — 7 base capabilities plus transactional notifications in
the image; **16 sellable Features** (`analytics-core` 1.1.0, `analytics-reports`,
`advanced-promotions` 2.0.0, `reviews-ratings`, `advanced-search` 1.1.0,
`customer-segmentation`, `loyalty-rewards` 1.1.0, `gift-cards`,
`marketing-automation`, `ai-reports`, `advanced-inventory`,
`restaurant-operations`, `multi-location`, `subscriptions`,
`external-marketplaces` 1.1.0, `log-shipping`); **no Draft identities left**; 5 plans,
two of them bundles. Seven Features run scheduled work declared in their own
manifests — `external-marketplaces` three of them since phase 24, and `multi-location` deliberately declares none.

There is one Feature outside the catalogue and it is not for sale:
`node-conformance`, which exists so the `node` runtime is demonstrated rather
than declared ([`adr/0032`](docs/adr/0032-a-feature-declares-its-runtime.md) §4).
[`docs/feature-catalog.md`](docs/feature-catalog.md) is the list.

---

## Phase 31 — Production hardening

The work after self-service is production-hardening, most of it raised by an
external architecture review. The full context — including where the code already
anticipates a risk the review named — is
[`docs/hardening-backlog.md`](docs/hardening-backlog.md); this is the phased,
tickable version of it. Priorities use the review's P0–P3.

### Product-owner decisions (unblock real automation)

- [ ] **Payment provider.** Seam `IPlatformPaymentProvider`. A **Stripe adapter**
  ships behind it (`StripePaymentProvider`, real webhook verification, off unless
  `PlatformBilling:Stripe:SecretKey` is set). Choosing it (or another adapter) and
  supplying live keys is what remains.
- [ ] **Hosting platform.** Seam `IInfrastructureAdapter`. A real adapter replaces
  `SimulatedInfrastructureAdapter` for `server`/`instance`/`domain-tls`. This is
  also where per-tenant **immutable-image** delivery lands
  ([`adr/0036`](docs/adr/0036-feature-delivery-runtime-install-versus-immutable-images.md)).
- [!] **External security review of the code-delivery path** (R16). The one item
  nobody inside the project can close.

### Hardening

- [~] **P0 — Signing-key custody.** **Done:** the KNIGHT-side signer has a KMS
  path (`FeatureArtifacts:Signer=kms` → `KmsArtifactSigner` over the `IKmsSigner`
  seam, `HttpKmsSigner` for a KMS proxy / Vault Transit; verification stays
  local), and the offline packaging tool signs through the same KMS when
  `KNIGHT_KMS_ENDPOINT` is set. **Remaining:** stand up a real KMS and configure
  it in both places.
- [x] **P0 — Delivery-model decision.** [`adr/0036`](docs/adr/0036-feature-delivery-runtime-install-versus-immutable-images.md):
  keep verify-then-install; adopt immutable per-tenant images as a hosting option
  behind `IInfrastructureAdapter`, not a rewrite; prefer `external_service` where a
  Feature can be one.
- [~] **P1 — Replace the Bash installers with IaC.** Built in
  `infrastructure/iac`: an idempotent Ansible role that is the provider-agnostic
  replacement for `install.sh`'s logic + a Terraform machine reference; YAML
  syntax-checked. Not ticked done until it has provisioned a live host end to end
  (no Ansible/Terraform in CI; hosting platform unchosen). `install.sh` stays the
  verified path meanwhile.
- [x] **P1 — PgBouncer** in front of PostgreSQL (compose, transaction mode, 6432).
  No app change; verified by running the API through it.
- [x] **P2 — Formal billing outbox.** The webhook writes an `ActivationOutboxEntry`
  in the activation's unit of work; `OutboxDispatcherWorker` drains it — a crash in
  the handoff no longer leaves a paid subscription with no store. Migration + tests.
- [x] **P2 — Agent least privilege.** `agent/deploy`: dedicated user, fully-sandboxed
  systemd unit, AppArmor profile. Authored; validate on a live host before enforcing.
- [!] **P2 — End-to-end secret rotation.** Blocked: it needs a **cross-repo** channel
  to push a rotated secret to the store (the agent reads its secret from config), so
  a KNIGHT-only sweep would lock stores out and is deliberately not built. See the
  backlog for the design.
- [x] **P3 — Tenant data export.** `GET /api/v1/me/export` + a portal "Download my
  data" button, and the deprovision `Export` step now auto-produces a durable
  snapshot (`IStoreExporter`) before purge — no operator step. Covered by the
  acceptance test and a store-export test.
- [!] **P3 — Push-based telemetry.** Cross-repo/infra (the store must emit OTLP; a
  collector must run) — sequenced with the hosting-platform decision.

Kept as-is per the review: the modular monolith, per-store isolation, architecture
tests and ADRs.

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

### Done
- [x] **`reviews-ratings`** — moderation by default, verified-purchase badges
      asserted by the caller rather than guessed, merchant replies, and a page
      of its own. Ships templates and a stylesheet inside the package, so it
      exercises asset delivery as well as code delivery
- [x] **`advanced-search`** — PostgreSQL full-text only, as the phase required.
      An index the store pushes documents into rather than a view over the
      catalogue, because a Feature may not read `apps.catalog` and because an
      index is what a search feature actually is. Weighted title/keyword/body
      ranking, facets, suggestions, and a prefix pass so a half-typed word still
      matches. Hostile queries are stripped rather than passed to `to_tsquery`
- [x] **`analytics-core` 1.1.0** — an optional `subject` on an event and
      per-subject aggregation over a window. Class A: a column with a default
      and one index, nothing rewritten, historical events left without an
      invented subject
- [x] **`customer-segmentation`** — five definitions seeded at install, computed
      from the analytics event stream. The first Feature whose data comes from
      another Feature, and the dependency is mandatory for a reason worth
      keeping: it may not import store business code, so the order table is not
      available to it and analytics is the only possible source
- [x] **The upgrade path, on a real store.** 15 events on analytics-core 1.0.0
      with analytics-reports installed against it, upgraded in place: events
      survived, the dependent kept answering, the migration reversed and
      re-applied
- [x] **The rollback drill.** All three new Features reverse to zero and
      re-apply. Plus a deliberately broken migration, which left nothing behind
      — same column count, no half-applied field, recorded unapplied, health
      check still passing
- [x] Six resolver tests on the **real** catalogue rather than synthetic
      fixtures: topological order, the 1.0.0 store being upgraded first, an
      unsatisfiable range refused rather than downgraded, and the diamond where
      reports and segmentation share one analytics-core
- [x] [`docs/phase-13-verification.md`](docs/phase-13-verification.md)

### Found by verifying it, and fixed
- [x] **The delivery path never told a store how to load a package.**
      `ManifestReader` parsed `app_label`, `installed_app` and the urlconf out of
      every manifest and dropped all four: they were never persisted, never in
      the job payload, never recorded by `enable`. The store guessed the module
      name from the slug, which was right only until
      [`adr/0029`](docs/adr/0029-one-slug-for-the-catalogue-and-the-package.md)
      shortened every slug in phase 12 — after which delivery would have
      registered an app no store can import
- [x] **No Feature's URLs had ever been mounted.** The same gap, and older:
      `analytics-reports` has declared a urlconf and shipped a `views.py` since
      phase 3.5 and its endpoint has never been reachable on any store.
      Publishing succeeded, the job ran, every step reported success. Found by
      opening a page in a browser, which is the argument for that being a
      release step rather than a courtesy
- [x] **Nothing tested the agent job payload at all**, which is how the above
      survived. Four integration tests on the delivery projection, four
      store-side regression tests on `enable`
- [x] **The local install command read a spelling no manifest has ever used**,
      and the hand-rolled YAML fallback flattened `django.urls.include` to
      `django.include` while claiming one level of nesting was all the schema
      allowed. Parser is indentation-aware now; PyYAML is a dev dependency so
      the real parser runs in development and CI
- [x] **A stale `build/` directory resurrects deleted files into an installed
      package** — a renamed migration shipped twice and Django refused two leaf
      nodes. Untracked and gitignored in phase 12, so a fresh checkout cannot
      hit it; a developer's machine can, and the error names neither cause nor
      directory
- [x] **Django 5.1 does not support Python 3.14.** Its test client cannot copy a
      template context there, so every test rendering a template errors. No
      existing test rendered one, so nothing had noticed. A `.venv312` matching
      CI now sits beside the 3.14 one

### Carried into phase 14
- [x] **The uninstall guard is untested.** Closed in phase 14: three integration
      tests, including that the dependency can be removed once the dependent is
      gone
- [x] `advanced-search` requires PostgreSQL and the manifest schema has nowhere
      to say so. Closed in phase 14: `compatibility.database` is parsed,
      validated and enforced by the resolver, so the refusal happens before an
      install rather than in a health check afterwards
- [ ] Fuzzy matching for `advanced-search` 1.1 — **still open, and deliberately
      so**. It wants `pg_trgm`, and a `CREATE EXTENSION` cannot be classified as
      Class A: another Feature may start using the extension, so a rollback
      cannot know whether dropping it is safe. Carried on to phase 16, where
      `advanced-inventory` raises the same question about extensions and the two
      are worth answering together rather than twice

---

## Phase 14 — Commercial foundations

**Exit criteria:** the a-la-carte proposition is real — a Custom customer can
assemble a meaningfully better store from published Features.

### Done
- [x] **`loyalty-rewards`** — points earned in **lots** with their own expiry and
      spent oldest-first, tiers on lifetime points so redeeming never demotes
      anybody, and a ledger nothing edits. Lots rather than a counter because a
      counter cannot answer either question a programme is actually asked: how
      many points are about to expire, and which points a redemption used
- [x] **`gift-cards`** — cards spendable across several orders, store credit per
      customer, two ledgers and no balance column anywhere. Codes come from a
      cryptographic source over an alphabet with no ambiguous characters, because
      a guessable code is a way to spend somebody else's money. Partial
      settlement is the normal case: 5.00 left against a 20.00 basket pays 5.00
- [x] Both manifests state **what their reversibility claim will stop being true
      of**: any later migration touching `points_remaining`, an amount or a
      status is Class C whatever its operations look like. Django can put a
      column back and cannot put money back
- [x] **Growth and Retention are plans**, not packages. Bundling is a commercial
      act, and the moment it becomes a fifteenth package there are two things to
      build, test and deliver for one thing to sell
- [x] [`docs/phase-14-verification.md`](docs/phase-14-verification.md)

### Carried in from phase 13, and closed
- [x] **The uninstall guard is tested.** Three integration tests: a dependency
      cannot be removed while something needs it, the dependent itself can be,
      and once it is gone the dependency follows — because "uninstall the
      dependent features first" has to actually work or the refusal is advice
      nobody can act on
- [x] **`compatibility.database` is a real constraint.** Parsed, validated
      against a closed list at publish, and enforced by the resolver.
      `advanced-search` declares `postgresql` and a store on another engine is
      refused *before* an install rather than by a health check afterwards. It
      was a comment until now, because the schema had nowhere to put it

### Found by verifying it, and fixed
- [x] **An `IntegrityError` poisons the transaction it happens in.** Both ledgers
      use a unique constraint for idempotency and then reported the balance — and
      on PostgreSQL a failed statement marks the whole transaction broken, so the
      duplicate branch could not run a query. A retried checkout raised
      `TransactionManagementError` instead of being told it had already settled.
      Every insert that can collide is in its own savepoint now
- [x] **`FOR UPDATE` cannot be applied to the nullable side of an outer join.**
      Locking the loyalty account with `select_related("tier")` made Django emit
      a LEFT JOIN on a nullable relation and PostgreSQL refused outright. The
      lock is `of=("self",)` — the account row, which is all that was contended
- [x] **A Feature's route was silently shadowed by the store's own.**
      `loyalty-rewards` mounted at `loyalty/`, which the storefront demo already
      serves, and the loader adds Feature URLs last on purpose — so an installed,
      working Feature answered somebody else's view with a 402. Both new
      Features now take the prefix that matches their slug, which is also the
      loader's default; and the loader is handed the store's own routes and
      **logs an error** naming the Feature, the prefix and the consequence. The
      store still wins, and is no longer quiet about it
- [x] **The storefront demo gated on slugs that stopped existing.**
      `apps/shop/views.py` checked `loyalty` and `analytics`, which
      [`adr/0029`](docs/adr/0029-one-slug-for-the-catalogue-and-the-package.md)
      replaced in phase 12 — so the demonstration of server-side entitlement was
      refusing customers who had genuinely paid

### Still open
- [ ] Fuzzy matching for `advanced-search` 1.1, carried through from phase 13 and
      on to phase 16. It needs `pg_trgm`, and a `CREATE EXTENSION` is not Class A
      — another Feature may start using the extension, so a rollback cannot know
      whether dropping it is safe. `advanced-inventory` raises the same question
      in phase 16, and answering it once for both is better than twice
- [ ] **Concurrency is argued, not proven.** The locks and constraints are the
      right ones and every idempotency path has a test, but nothing runs two
      transactions at once to watch them contend. That needs a harness with real
      parallel connections, and it is worth having before the first customer
      sells a gift card
- [ ] Neither ledger is reachable from the dashboard, so issuing a card, voiding
      one, granting credit and adjusting points are shell commands. Consistent
      with KNIGHT not being a store's business backend, and still a gap for
      whoever runs a shop

---

## Phase 15 — Automation

**Exit criteria:** KNIGHT can act on a schedule on a store's behalf, with
per-customer cost bounded and auditable.

### Done
- [x] **Manifest-declared workers.** A Feature declares a scheduled job, KNIGHT
      delivers the declaration with the install, and the store runs it — so
      installing a Feature installs its schedule. `hourly | daily | weekly`
      rather than a cron expression: a cron string is a parser, a timezone
      question and a support surface, and the word travels so the store decides
      what it means for its own timezone
- [x] Validated hard at publish, because a worker is code KNIGHT causes a store
      to run on a timer with nobody watching. A malformed entrypoint would fail
      silently every hour for as long as the Feature is installed; two workers
      of one name are refused because a store records the last run per name
- [x] The runner is built around what goes wrong on a timer: one misbehaving
      worker loses its own run and nothing else, every run is recorded including
      the failures, and a failure does **not** move the next run forward — a job
      failing for three days is still due rather than quietly rescheduled. A
      corrupt run history is refused rather than read as empty, because reading
      it as empty would mail a store's whole customer list twice
- [x] `loyalty-rewards` 1.1.0 moves its expiry onto a declared worker, as phase
      14 said it would
- [x] **`marketing-automation`** — welcome, post-purchase, abandoned-cart and
      win-back campaigns, audiences from `customer-segmentation` and triggers
      from `analytics-core`. The first Feature whose dangerous failure is
      *sending*, so: consent is a fact the store records rather than something
      inferred, suppression is keyed on the address so a new customer id is not
      a fresh start, and a unique constraint per campaign and subject is what
      makes double-mailing impossible. Installs with every campaign off and the
      provider in recording mode
- [x] **The first named-not-valued secret, end to end.** Declared in the
      manifest without a value, delivered over the install channel, read through
      `config.secret()`, reported by name only, and absent from every error
      message. Tested both ways round
- [x] **`ai-reports`** — findings computed by arithmetic and narration generated
      optionally. Deterministic, auditable and free, and correct with no provider
      configured at all: a number a merchant acts on is never something a model
      produced
- [x] **Cost controls before it is sellable, not after.** A monthly token and
      cost cap, priced *before* the call so an over-budget store never makes it,
      the window rolled over on read rather than by a job, and the findings
      surviving a refusal — so a capped store still gets the part it can act on.
      Visible to the merchant at `/ai-reports/usage/`
- [x] **Privacy settled and written down**:
      [`adr/0030`](docs/adr/0030-what-store-data-may-reach-a-model-provider.md).
      Only aggregates KNIGHT's own arithmetic computed may leave a store, by
      **allow-list** — a deny-list is one new field away from leaking. A finding
      may not carry a customer reference at all, which is why the concentration
      finding says "62% from a single customer" and not which one
- [x] [`docs/phase-15-verification.md`](docs/phase-15-verification.md)

### Found by verifying it, and fixed
- [x] **A test module that could not be imported on a base store.**
      `test_ai_reports.py` annotated a helper with a type that only exists when
      the Feature is installed, and Python evaluates annotations at
      function-definition time — so the module errored instead of skipping.
      Caught only by the run with **no Features installed**, which is exactly
      what that configuration is for
- [x] **An unused statistical helper that failed its own motivating example.** A
      z-score outlier rule where the single outlier inflated the standard
      deviation enough to hide itself — deviation 72, threshold exactly 72.0.
      Removed rather than tuned: dead statistical code that fails on the case it
      exists for is worse than none, and anomaly detection should arrive with a
      finding that uses it
- [x] **A `DateField` defaulted to `timezone.now`**, which returns a datetime.
      Django coerced it on save so the mistake survived a round trip and
      surfaced only in the API response, where a usage window printed a full
      timestamp
- [x] **The packaging tool could not read a `workers:` block** — the third phase
      running in which a hand-rolled YAML fallback was the thing that broke. It
      handled inline sequence items, because `dependencies.features` is written
      that way, and raised on block-style ones. Raising rather than guessing was
      right; it reads both now
- [x] **And fixing that exposed a worse bug, present since phase 3.5.** The
      inline-map reader split on every comma, so
      `{ slug: analytics-core, version: ">=1.0.0,<2.0.0" }` parsed as a slug, a
      version of `">=1.0.0` and a third key called `<2.0.0"`. Nothing noticed
      because the tool never resolves dependencies — so it built a **correct
      artifact from a manifest it had misread**, which is the worst shape a
      parser bug can take. The split is quote-aware now

### Still open
- [ ] **Neither `api` provider calls a vendor.** Both read their named secret,
      validate it and refuse clearly. Wiring one is an integration against a
      real account under a real agreement, and inventing it here would mean
      shipping code nobody has watched work. Which provider, under what
      agreement, in which jurisdiction is a commercial and legal question
      [`adr/0030`](docs/adr/0030-what-store-data-may-reach-a-model-provider.md)
      names rather than answers — and it has to be answered before either is
      wired up
- [ ] **Abandoned-cart needs an event the base store does not emit.** The
      trigger reads `cart.abandoned`; a store wanting that campaign has to emit
      it. The Feature names the event rather than inventing one
- [ ] **Concurrency is argued, not proven** — carried from phase 14. The worker
      runner's isolation and the send constraint are the right shapes and every
      idempotency path has a test, but nothing runs two transactions at once to
      watch them contend

---

## Phase 16 — Operational expansion ✅

**Exit criteria:** KNIGHT is credible for a merchant with real operations
behind the shop. **Met** — see
[`docs/phase-16-verification.md`](docs/phase-16-verification.md).

- [x] **How a `CREATE EXTENSION` is classified**, once and for both callers.
      [`adr/0031`](docs/adr/0031-database-extensions-are-declared-not-migrated.md):
      declared in the manifest, created before migrations, never dropped —
      because a rollback cannot know whether another Feature has started using
      the extension in the meantime. `advanced-search` 1.1.0 and
      `advanced-inventory` both declare `pg_trgm`, which is the case that made
      the rule necessary
- [x] **`advanced-inventory`** — stock as an append-only ledger. No quantity
      column anywhere: what a shop has is the sum of what arrived minus what
      left. Available is on hand minus what is held, holds end by time rather
      than by state, and `reserve()` is demonstrated under concurrency rather
      than argued
- [x] **`restaurant-operations`** — tables and sessions, kitchen tickets with
      states finer than an order's, preparation times per dish and per station,
      and pickup slots that are booked under a row lock rather than displayed. A
      promise is the longest dish plus the queue, never the sum
- [x] **`multi-location`** — branches with their own timezones and opening
      hours, staff rotas, per-branch menu exceptions, and routing decided once
      and written down. **The migration risk was never there**: a Feature owns
      only its own tables, so this one could not have added a `location` column
      to anybody else's — which is why both of the Features above carried theirs
      from their own 1.0. Installing it migrates nobody's rows
- [x] Four defects found by verifying rather than by testing, and fixed: the
      store's fallback manifest reader read `extensions: []` as the truthy
      string `"[]"`; the packaging tool's own reader refused any manifest with a
      prose comment ending in a quoted phrase, which only ever showed on a
      runner; and every dashboard list screen was rendering page one as though
      it were the whole collection — which this phase made visible by pushing
      the catalogue past twenty-five Features. The fourth was read rather than
      run: a kitchen ticket number that rolls over at four digits cannot also be
      unique across all history. The packaging tool now has a `selftest` that CI
      runs before it builds anything

---

## Phase 17 — Recurring revenue and external integrations ✅

**Exit criteria:** the last two Feature families ship, and the catalogue is
complete. **Met** — see
[`docs/phase-17-verification.md`](docs/phase-17-verification.md).

- [x] **R26 answered `yes` by the product owner, and built.** A manifest declares
      `runtime:` and carries a block named for it, and the three facts a store
      needs — namespace, module, mount — are named the same way whatever the
      runtime is, so the wire and both installers never learnt a second
      vocabulary ([`adr/0032`](docs/adr/0032-a-feature-declares-its-runtime.md)).
      Done **first**, so the two new Features were authored in the new shape
      rather than migrated into it. No database migration was needed: the wiring
      was already read from the signed manifest rather than duplicated into
      columns
- [x] **`stores/node-reference-store`** — a store that is not Django, taking
      delivery of a real signed artifact in CI on every push. `node` is a runtime
      because a store has received a Feature over it, not because a validator
      lists it. Dependency-free, including its zip reader, because "add this
      package" shows nothing to a team writing their store in Go
- [x] **`subscriptions`** — recurring orders, a state machine that pauses and
      resumes, retries and failed-payment handling. Arranged around one unique
      index: a period is opened before it is charged and numbered per
      subscription, so a cron firing twice, a webhook delivered twice and an
      operator running the worker by hand all end at the same constraint.
      Removing the row lock does not break it, which is the point
- [x] **`external-marketplaces`** — connections, a queue that records before it
      sends, idempotency on the *partner's* event id, retries that widen and then
      abandon, per-provider adapters and reconciliation that reports without ever
      fixing. Deliberately last, and it earned it
- [x] Five defects found by verifying rather than by writing: two clock bugs in
      billing, a node Feature reading its configuration from the wrong directory,
      resuming inside a paid period charging for that period twice — found only
      in a browser — and, found only by CI, two Feature globs that assumed every
      Feature is a Python distribution. The second of those would not have
      stopped anything: `knight_install_local` bypasses `preflight`, so it would
      have registered a node Feature into a Django store's INSTALLED_APPS and the
      store would have failed to start

Carried out of phase 18:

- [~] **Automate the phase-18 run** and **roll back across two genuinely
      different schemas** — both are phase 19, above
- [ ] **The health poller does not capture the runtime block**, only the
      heartbeat does. A store KNIGHT polls but which never heartbeats stays
      uncertifiable for delivery
- [ ] **Domain verification was never exercised**, and nothing in the delivery
      path gates on it: a store can be installed into while still `Pending`

Carried out of phase 17, none of it blocking:

- [x] **KNIGHT does not know a store's runtime** — closed in phase 20. The
      heartbeat carries `runtime.name`, the resolver checks it before every
      other compatibility fact, and a mismatch is refused with its own code
      before a job exists. It turned out to be worse than noisy: a node store
      could not be planned against *at all*, because compatibility was decided
      on Python and Django versions it has no way to report
- [ ] **No vendor is wired to any of the four Features that honestly refuse
      without one**: `marketing-automation`, `ai-reports`, `subscriptions` and
      `external-marketplaces`. Each reads its credential, validates it and
      refuses at the point the call would be made. Wiring one is an integration
      against a real account under a real agreement, and it is a commercial
      decision before it is an engineering one
- [ ] **OAuth token refresh is not automated.** `external-marketplaces` stores an
      expiry and marks a connection expired when an adapter reports a credential
      failure, which stops a hundred retries against a revoked token. Refreshing
      needs the vendor flow above
- [ ] **Webhook authentication is the store's**, deliberately — every partner
      signs differently — but no store in this repository demonstrates one. A
      worked example in the reference store would be worth having

Carried out of phase 16, none of it blocking:

- [ ] **Real-time order updates for the kitchen board.** `restaurant-operations`
      ships polling endpoints and no push channel. A board that refreshes every
      two seconds is genuinely fine, and a channel between a store and its own
      staff is the store's to build rather than a Feature's to impose — but the
      phase-16 scope named it, so it is written down rather than quietly dropped
- [ ] **Routing has no geography.** A `Location` carries latitude and longitude
      and no rule kind uses them, so "nearest branch" cannot be expressed. The
      closed list of four rule kinds was deliberate; adding a fifth is additive
- [ ] **Nothing joins a branch's menu to its stock.** `multi-location` can say
      Soho does not sell the burger and `advanced-inventory` can say Camden has
      none left, and no party puts the two together. That join belongs to a
      store's checkout — the only party that knows what it is about to sell —
      and doing it inside either Feature would break the rule the catalogue
      rests on
- [ ] **Concurrency is proven for stock, slots and billing, and argued
      elsewhere.** The three claims that can oversell or overcharge somebody have
      two-connection races behind them. Ticket transitions, the routing decision
      and the marketplace queue rely on locks and constraints that have
      idempotency tests but no thread racing them

---

## Phase 18 — The catalogue through its own delivery path ✅

**Exit criteria:** a store goes from empty to **fully entitled and installed
through KNIGHT's own delivery path** — signed artifacts, real jobs, real
claims — with `knight_install_local` used nowhere, and a Feature upgraded and
rolled back on it afterwards.

Why this and not more Features: every Feature in this repository was authored and
tested with `knight_install_local`, which exists **precisely because it bypasses
the delivery path**. That is the right tool for writing a Feature and it means
the delivery engine has never carried the whole catalogue. Phase 13 put three
Features through it; sixteen is a different question, and it is the question the
entire product rests on.

Phase 17 already produced the evidence that the two paths diverge rather than
merely differ. The runtime check lived in `preflight`, where a delivered package
is checked — and had to be added to `knight_install_local` separately, because
they are two code paths that had silently drifted. Nobody found that by reading;
CI found it. Whatever else has drifted is in the half nobody exercises.

- [x] **Published the whole catalogue** — 15 packages built, signed with a real
      key, uploaded, registered and published. `node-conformance` is refused,
      correctly: it has no catalogue identity, which is what "not for sale" means
- [x] **Set a customer up through the API** — customer, store, credentials, plan,
      subscription, entitlements. Two refusals along the way were the API being
      right rather than gaps
- [x] **Installed 13 Features by claiming jobs.** The other two were refused
      precisely: `advanced-promotions` 2.0.0 wants a store version this store
      does not have, and `ai-reports` needs dedicated infrastructure
- [x] **Upgraded** `reviews-ratings` 1.0.0 → 1.0.1 → 1.0.2 with no version named,
      which had been a silent no-op
- [x] **Rolled it back** to 1.0.1 with a row in its table throughout, and the row
      survived — which is what `adr/0016`'s Class A promise has always claimed
      and nothing had ever checked
- [x] **Withdrew an entitlement** and watched a Disable job appear unasked: the
      Feature stopped serving, kept its data, and no other Feature moved
- [x] **Eight defects found and fixed.** Six made delivery impossible: no store
      could report its runtime or its database, a delivered package was never
      importable, a Feature was registered too late to be migrated, rollback
      restored from a deleted backup and migrated to zero — destroying the
      merchant's data while reporting success — and KNIGHT could never record a
      rollback at all. Two were about what an operator is told: a Feature that
      could not be installed was reported as one that does not exist, and the
      first fix for that answered 500

---

## Phase 19 — The delivery drill runs itself

**Exit criteria:** the journey phase 18 walked by hand — publish, onboard,
connect, install, upgrade, roll back, withdraw — runs as **one command, in CI, on
every push**, and fails loudly when any step of it regresses.

This phase is the one phase 18 nominated in its own write-up, and it is taken at
its word. Eight defects lived in the delivery path for six phases. Every one of
them now has a test, and not one of those tests would have found the *next* one,
because what they check is the code each defect happened to be in rather than the
path all eight shared. What finds a ninth is the journey, run.

The rollback half deserves better than phase 18 gave it. That drill upgraded
between two versions whose migrations were identical, so the reverse migration
had nothing to undo: it proved the tables survive and the version numbers move,
and said nothing about whether a real down-migration works. The drill built here
carries its own Feature with two genuinely different schemas, so the rollback has
something to reverse and the data has something to survive.

- [x] **A drill that runs the whole journey** against a real API and a real
      store, asserting at every step and exiting non-zero on the first thing that
      is not true
- [x] **Its own two-version Feature**, whose 1.1.0 adds a column its 1.0.0 does
      not have, so upgrade and rollback are real schema changes rather than
      version-number changes. Created through the API at drill time rather than
      seeded, so the sellable catalogue stays free of test fixtures
- [x] **Real catalogue Features installed too**, so the drill proves the actual
      packages still install and not only that the machinery moves
- [x] **In CI**, with its own job: Postgres, the API, the store, and the drill
- [x] Fix everything it finds — it found a ninth defect on its first complete
      run: a rollback restored the previous package **before** reversing its
      migrations, and Django can only unapply a migration whose file it can still
      see, so the code rolled back and the database did not. Two more turned up
      once it ran somewhere that was not a developer's machine: the packaging
      tool's fallback manifest reader could not read an inline list, so a
      manifest this repository ships could not be published from a clean
      checkout, and the workflow signed artifacts under a key id KNIGHT was not
      configured to trust
      ([`phase-19-verification.md`](docs/phase-19-verification.md))

---

## Phase 20 — The second runtime, through the real path, and the refusals

**Exit criteria:** a store that is **not** Django is entitled, planned against,
and takes delivery of a Feature **through KNIGHT's own job path** — handshake,
heartbeat, claim, report — and the drill asserts what delivery is supposed to
**refuse** as carefully as it already asserts what it is supposed to do.

Two halves, and they are one phase because neither is finished without the other.

**The runtime is still half a promise.** `adr/0032` settled that a Feature
declares its runtime, and the node store proves a node package can be unpacked,
mounted and health-checked. What has never happened is a node store *asking
KNIGHT for work*. Its `apply-job` reads the payload from a file, which was the
right boundary to draw in phase 17 and is the wrong one to leave: KNIGHT cannot
plan against a node store at all today, because compatibility is decided on the
Python and Django versions a node store has no way to report. Every Feature would
be refused as `IncompatibleStore`, which is the same defect phase 18 found for
Django stores, still live for the other runtime.

So KNIGHT has to learn what a runtime *is*: check the store's runtime **first**,
refuse a Feature built for another one by name, and apply only the version checks
that belong to the runtime in hand. That closes the item phase 17 carried and
phase 18 and 19 both left open.

**And the drill only knows how to succeed.** Twenty-four assertions and every one
of them checks that something worked. A delivery engine is judged at least as
much on what it refuses — a tampered artifact, a Feature for the wrong runtime,
something the customer is not entitled to — and those paths currently rest on
unit tests, which is exactly the position the whole delivery path was in before
phase 18.

- [x] **KNIGHT knows a store's runtime**, reported on the heartbeat and checked
      before anything else, with a failure of its own that names both sides
- [x] **Only the checks that belong to the runtime**: a node store is not asked
      for a Django version, and a Django store is not asked for a node one
- [x] **The node store claims jobs over HTTP** — handshake, heartbeat, claim,
      report steps, report the outcome — against a real KNIGHT
- [x] **A node store takes delivery of a Feature end to end**, in the drill and
      in CI
- [x] **The drill asserts refusals**: a bad signature stops at `verify` and is
      reported as a failure rather than a silent success, a Feature for the wrong
      runtime never becomes a job, and an unentitled Feature is refused
- [x] Fix everything it finds

---

## Phase 21 — A third runtime, and two real stores

**Exit criteria:** the two customer stores that exist are connected to KNIGHT
and can take delivery of a Feature.

Both turned out to be **ASP.NET Core**, which was neither of the runtimes that
existed. That is the useful kind of surprise: `adr/0032` claimed the delivery
path was never Django's, and this is the first time anybody tested the claim
against a runtime nobody had planned for.

It cost the enum one line, the manifest reader one method and the resolver one
case. The store side is a library — `Knight.StoreAgent` — shared by every .NET
store rather than an integration written per project, which is the answer to
"do I write an agent for each shop": one per stack, not one per project.

- [x] **`dotnet` is a runtime KNIGHT can deliver to**, with the three neutral
      names spelled namespace, assembly and mount type
- [x] **A .NET store agent**, written once: handshake, heartbeat, claim, the
      fifteen verbs, report. Nineteen tests, most of them refusals
- [x] **BojanStore wired**, building and green on its own 944 tests
- [x] **Phonix wired**, building
- [ ] **Phonix merged** — the branch exists and there is no write access to
      that repository from here; the patch is on the desktop and the change is
      three files plus a vendored directory
- [ ] **Neither store has been driven end to end against a running KNIGHT.**
      Both compile with the agent in and the agent's own suite covers the whole
      pipeline, but no artifact has been delivered to either. Nothing is
      finished until one has been, which is what phases 18 to 20 were about
- [ ] **A .NET Feature to deliver to them.** The catalogue is Django packages;
      a `dotnet` Feature exists only as a manifest shape and a test fixture

---

## Phase 22 — Features as services, not only as packages ✅

**Exit criteria:** a Feature can be delivered as a signed configuration rather
than as code, all three agents act on one, and nothing that worked before stops.

Building the third store agent was the argument for this. It cost a library,
fifteen verbs and nineteen tests — and the node agent turned out to have been
missing three verbs for three phases and three more for four. 150 Features across
three runtimes is 450 packages to build, sign, install, migrate and roll back,
each inside a store we do not operate, holding that store's database handle.

- [x] **`architecture: external_service`**, with `service`, `webhooks`,
      `api_proxies` and `ui_mounts`; the in-process blocks refused on one
- [x] **Signed configuration instead of an archive** — still hashed, still
      signed, still verified before the store acts
- [x] **Six external pipelines**, built entirely from verbs all three agents
      already implement, with a test that fails if that ever stops being true
- [x] **All three agents** register rather than unpack, and each validates the
      events and slots against its own catalogue
- [x] **`subscriptions` 2.0.0** as the proof of concept
- [x] **The drill walks it**, asserting the absences: no table, no package
      directory, no migrate step
- [x] **[`adr/0033`](docs/adr/0033-api-driven-features.md)** and
      [the verification](docs/phase-22-verification.md)

Carried, and each of them written down rather than implied:

- [ ] **Nothing runs at the other end.** `subscriptions` 2.0.0 names a service
      that does not exist. The delivery path is proven end to end; standing the
      service up is a deployment and a separate piece of work
- [ ] **No event has actually been delivered and no request actually proxied.**
      Both are unit-tested against a mocked transport. The reference store has
      no queue, and one that picked Celery would be telling every store to run it
- [ ] **The `at-least-once` retry policy is named, not built**
- [ ] **A store cannot pin a service version.** Versioning the configuration
      does not version the service behind it: an author can change behaviour for
      every store at once, which is the same property that lets them fix a bug
      for every store at once
- [ ] **The other fifteen Features are unconverted**, deliberately. In-process
      delivery is not deprecated by this: it is still the right answer for
      anything that must be inside the store's transaction, and there is no way
      to be inside a transaction over HTTP

---

## Phase 23 — The live service layer ✅

**Exit criteria:** an order placed in the reference store is received by a real
subscriptions service, and a merchant's request reaches that service through the
store's proxy and comes back — both over the real delivery path, asserted by the
drill. **Met.**

**Verification:** [`docs/phase-23-verification.md`](docs/phase-23-verification.md)
— the six defects that only two processes disagreeing could show.

Phase 22 built the whole API-driven architecture against nothing.
`subscriptions` 2.0.0 named a service at `https://subscriptions.knight.dev` and
no such thing existed: the store registered webhooks it would never deliver and
proxy routes that forwarded to a host that did not answer. Every test passed and
not one byte had crossed between a store and a service.

- [x] **The subscriptions service** — `services/subscriptions/`, an ordinary
      Django application with its own database. The domain moved unchanged; what
      it lost is the store's database handle and what it gained is its own
- [x] **Every row belongs to a store.** One deployment serves every shop, so a
      reference is unique *within* a store rather than globally — two shops both
      numbering from `SUB-1` is normal, and a global unique index would have
      made the second shop's first subscription fail to create
- [x] **Its half of the contract** — HMAC verification, skew window, replay
      rejection, four receivers, the proxied APIs, `/healthz`. Seventeen tests,
      most of them attacks
- [x] **The delivery queue** — a table, a worker, exponential retry over roughly
      twelve hours, then a dead letter that is kept. `at-least-once` stopped
      being a word in a manifest
- [x] **The store publishes**, through the façade rather than by importing the
      bus — the boundary test caught that within a minute of it being wrong
- [x] **`docker-compose.yml`**, so one command stands the picture up
- [x] **Drill steps 12 and 13**, and CI runs them on every push

### Found and fixed while verifying phase 23

- [x] **The store signed one path and the service verified another.** The proxy
      signed over the path on the *store*; the service builds its canonical
      string from the path it received. Every proxied request would have failed
      with a signature that was perfectly correct about the wrong thing. The
      delivery worker had the same defect, from slicing a URL rather than
      parsing it
- [x] **The store identified itself by the Feature's name.** `X-Knight-Store`
      was set from `contract.slug` in both callers, so every request was refused
      as `store.unknown`. The header is now set inside `sign()` — a caller
      cannot forget it — and carries the store id KNIGHT issued rather than a
      slug a merchant can rename
- [x] **The webhook demanded something the store cannot know.** `order_placed`
      required a period sequence, and a period is the service's idea. The store
      now carries an opaque reference and the service works out what it means
- [x] **Business code reached past the façade**, and the store's own boundary
      test caught it. That test has paid for itself twice now
- [x] **The drill read a 401 as "not started"**, and waited its full timeout for
      a store that had been serving the whole time
- [x] **A stopped server kept serving.** `stop()` returned before the socket was
      free, the restart could not bind and exited, and the old process kept
      answering — so the drill tested a urlconf built before the Feature was
      installed. It waits for the port now, and `start()` notices a process that
      died rather than trusting whatever answers

### Not done

- [ ] **The shared secret is set by hand.** Both ends have to agree and this
      phase creates both. KNIGHT issuing it per store and rotating it without an
      outage is phase 24
- [ ] **The service is not deployed anywhere.** It runs in `docker compose` and
      in CI; putting it on a host is phase 27
- [ ] **`order.refunded` has a receiver and no publisher** — the base store has
      no refund flow yet. Declared and wired, not exercised
- [x] **The billing loop is closed** — `knight_generate_subscription_orders`
      asks the Feature what is owed an order, places them and reports the
      numbers back, and does it the same way whether `subscriptions` is a
      package or a service. Ten tests. The **drill** still places an order
      directly, because step 12 exists to prove the event path; walking bill →
      generate → report is a natural fifteenth step and is not written
- [ ] **Nothing rotates the nonce table.** `forget_old_nonces` exists and is
      tested and nothing runs it on a timer. One cron entry, and it belongs with
      phase 26

---

# The rest of the road

Six phases from here to a release decision. Every one has an exit criterion that
is a **demonstrable fact** rather than a list of work, and a gate that must be
green before the next starts — the discipline phases 18 to 23 used, which is the
only thing on this project that has reliably caught defects.

[`docs/roadmap.md`](docs/roadmap.md) is the same road drawn end to end, with the
dependency order and the five decisions that are the product owner's rather than
an engineer's.

**A note on counting.** Work carried from an earlier phase appears **twice** in
this file, on purpose: once in the phase that recorded not doing it, because
that is the historical record and rewriting it would be a lie about what
happened, and once in the phase below that owns closing it. So the unticked
boxes in this file are not a count of remaining work — the roadmap has that
count, sorted by who can actually close each item.

---

## Phase 24 — Secrets, identity and rotation

**Exit criteria:** each store has its own shared secret with each service,
issued by KNIGHT and rotatable without an outage, and a store whose entitlement
was revoked cannot call the service at all.

Today the secret is one environment variable an operator sets by hand on the
store and one row an operator writes on the service. That is correct for one
store and one service, and it is an incident at ten: nobody can rotate it
without downtime, nobody can tell which stores share one, and revoking an
entitlement stops the store forwarding without stopping the service answering.

Phase 23 said so in its own drill — the constant is called
`drill-shared-secret-not-for-a-deployment` — rather than pretending otherwise.

- [x] **KNIGHT issues the secret**, per (store, feature), delivered down the
      configuration-secret path every other secret travels. Never in the
      manifest, which names the variable and nothing else
- [x] **Rotation with overlapping validity.** A store's secrets are rows with
      lifetimes and any currently valid one verifies, so issuing a new one sets
      the old ones expiring rather than replacing them. An expiry only ever
      moves downwards; an overlap of zero is allowed and is what a leak needs
- [x] **The service learns about stores from KNIGHT.** Four routes under
      `/knight/`, signed with a control secret that is not any store's —
      authenticating them as a store would be circular, because issuing that
      store's secret is what they are for. Unconfigured refuses everything
- [x] **Revocation reaches the service.** Withdrawing an entitlement disables
      the installation *and* ends the store's secrets, so a store whose registry
      is stale or restored from a backup is refused by the service itself
- [x] **The nonce table is rotated** — `knight_maintain`, hourly, as a compose
      sidecar. It also throws away the value of secrets nobody can use any more,
      keeping the row and its dates
- [x] **OAuth token refresh is automated** for `external-marketplaces` 1.1.0: an
      hourly worker renews what is close to expiring, leaves a long-lived token
      alone, and marks a connection expired when the other end rejects the
      refresh token rather than retrying into a rate limit

**Gate: passed.** Drill step 14 rotates a live secret and finds both valid, sees
the store serve with the new one without a restart, and watches a withdrawn
entitlement refused by the service — 401 from the service itself, whatever the
store's own registry says. [`docs/phase-24-verification.md`](docs/phase-24-verification.md).

### Not done

- [ ] **Nothing calls the issue endpoint on install.** An external Feature's
      first credential is still asked for by hand. Small, and it belongs with
      phase 25 where a second real store makes the omission obvious
- [ ] **Nothing rotates on a schedule.** Rotation being possible was the missing
      property; a ninety-day policy is phase 26's operational work
- [ ] **The control secret has no rotation story of its own.** One value per
      Feature, held by KNIGHT, changed in two places at once — the same problem
      one level up, and smaller, because there is one holder rather than a fleet

---

## Phase 25 — The two real stores, end to end

**Exit criteria:** BojanStore takes delivery of a Feature from a running KNIGHT
and serves it, verified in a browser.

Closes the open half of phase 21, and it is much cheaper than it was when that
phase was written. **An `external_service` Feature has no runtime**, so a .NET
store can take delivery of one without a single line of .NET Feature code
existing — which took the largest item off phase 21's critical path without
anybody doing anything.

- [x] **BojanStore connected** — and not by an environment variable. The
      credential is entered on its own settings screen, the agent reads it on
      its next pass and the handshake follows without a restart, because the
      person who owns a shop cannot restart a container and "send me your client
      secret" is a worse answer
- [x] **`subscriptions` 2.1.0 installed on it and serving.**
      `GET /api/features/subscribe/` on that shop answers with the service's
      own reply; a staff route is 403 and an undeclared method 405, both refused
      by the store before anything is forwarded
- [x] **Driven in a browser**, and it found three defects nothing else would
      have: a delivered Feature that was recorded and never served, a shared
      secret that arrived and was thrown away, and a forwarded request that was
      correctly signed and did not say which store it came from
- [ ] **Phonix the same** — no write access to that repository from here, and
      the patch on the desktop is the product owner's to apply. Carried
- [ ] **A `dotnet` Feature**, to prove the in-process path on that runtime.
      BojanStore took delivery of a Feature that is a *service*, which needs no
      runtime at all, so this is still the one runtime whose in-process delivery
      has never been exercised against a real store
- [ ] **UI mounts are listed, not mounted.** The panel shows where a Feature's
      screens want to hang and nothing renders them. The next honest piece of
      work on this seam

**Gate: passed for BojanStore.** A Feature installed on a store whose code is
not in this repository, serving through it, verified in a browser rather than in
a test. [`docs/phase-25-verification.md`](docs/phase-25-verification.md).

---

## Phase 26 — Operating it

**Exit criteria:** a failed webhook delivery, a proxy 502 and a job stuck in
`Running` are each visible on a screen and each raise an alert, without anybody
reading a log.

The architecture now has three new ways to fail silently — a delivery that
dead-letters, a service that stops answering, a proxy that times out — and none
of them is visible anywhere. A dead letter is the record that a Feature a
merchant pays for did not hear something, and at the moment the only way to find
one is to run `knight_deliver --dead-letters` and know to.

- [x] **A failed delivery and an unreachable service are alerts.** The store
      reports what raises no exception — a delivery that used every attempt, a
      service that did not answer, a Feature with no shared secret — on the
      channel it already has, and KNIGHT raises `delivery.dead_lettered`
      (critical) and `service.unreachable` (warning) from the sweep that already
      runs. Grouped per store, Feature and kind, because a service down for an
      hour is one fact
- [x] **Alerting rules, and a runbook per alert** —
      [`docs/runbooks.md`](docs/runbooks.md), one entry each, with what to look
      at, what to do, and when it is safe to ignore. Writing them found two
      things that were not true: the dead-letter listing had no ids, and there
      was no way to replay one. Both exist now (`knight_deliver --replay`)
- [ ] **Delivery metrics as counters**: attempted, delivered, retried,
      dead-lettered, by feature and by store. The alerting path and the reports
      under it exist; a metrics view does not
- [ ] **A metrics scrape endpoint.** The meter is published and any collector
      could read it; there is nothing to read it from. Carried from phase 7
- [ ] **Redis instrumentation**, carried from phase 7
- [ ] **Dashboard screens** for deliveries and the dead-letter queue, and for
      the two ledgers phase 14 left unreachable — issuing a gift card, voiding
      one, and reading a loyalty balance are all API-only today
- [ ] **A reusable job-progress component.** The events are broadcast and the
      screens refetch; nothing renders per-step progress. Carried from phase 6
- [ ] **Alerting rules, and a runbook per alert.** An alert without a runbook is
      a page at three in the morning that begins with reading source code
- [ ] **`server_metrics` partitioning**, carried from phase 4: retention works
      and the table is one table
- [ ] **Manual merge and split of error groups**, carried from phase 5, where
      `adr/0013` names it as the mitigation for a grouping heuristic that will
      sometimes be wrong
- [ ] **Log search, filtering and export**, carried from phase 3 and still open
      after phase 7 passed without it
- [ ] **The health poller captures the runtime block**, carried from phase 17. A
      store KNIGHT polls but which never heartbeats is still uncertifiable
- [ ] **Concurrency proven rather than argued.** Recorded three times — phases
      14, 15 and 17 — and it is the kind of thing that is fine until it is an
      incident. The locks and the constraints are the right ones; nothing has
      run two workers at the same row and watched

- [ ] **The .NET agent reports none of this.** BojanStore's proxy returns its
      502 and says nothing to KNIGHT. The library has the same three moments to
      report from and they are not wired

**Gate: passed.** A delivery pointed at a dead port was given up on, reported,
grouped and raised as a critical alert naming the store and the Feature — read
off the alerts screen rather than out of a log.
[`docs/phase-26-verification.md`](docs/phase-26-verification.md).

---

## Phase 27 — Deployment

**Exit criteria:** KNIGHT, the reference store and one service deploy from CI to
a real host, with TLS, scheduled backups going offsite, and a rehearsed way
back.

The server half is not blocked and can go first. The image half waits on the
hosting decision, which is one of the five things only the product owner can
settle ([`docs/roadmap.md`](docs/roadmap.md) §7).

- [x] **`install-agent.sh`** for the servers that host stores, **and
      `install-store.sh`** for a Django store on one. Both re-runnable, and both
      keep what an upgrade must not lose: an agent's enrolment, a store's
      environment file, its delivered Features and its database. Carried from
      phase 11
- [ ] **Docker images and the deploy stages** of
      [`deployment.md`](docs/deployment.md) §8 — *needs the hosting decision*
- [~] **An offsite copy of the nightly dumps.** `knight-offsite.sh` ships the
      newest dumps to rsync, S3 or a mount and verifies each one — against its
      own manifest before sending, against the remote size after. The installer
      writes the timer and **does not enable it** until somebody sets
      `KNIGHT_OFFSITE_TARGET`, because where a database of customer records may
      be copied to is a custody decision and not an installer's. *Still needs
      that decision to actually run.*
- [ ] **The installer against a real cloud VM with real DNS** — *needs a VM and
      a domain*. The container run exercised everything except certificate
      issuance
- [ ] **Provisioning automation** — creating the machine, building the instance,
      wiring DNS and TLS. Carried from phase 9 and blocked on the same decision
      as the images
- [x] **A restart strategy that does not drop live traffic.** The store's unit
      is socket-activated, so the socket outlives the service: `systemctl reload`
      starts new workers, retires the old ones once they are idle, and never
      closes the listening socket. Carried from phase 3.5
- [ ] **Signed agent releases and a self-update path**, carried from phase 4.
      An agent that cannot update itself is a fleet somebody updates by hand

**Gate: not run, and not runnable from here.** It needs a host, a domain and a
place to put backups — three of the five decisions that are the product owner's
([`docs/roadmap.md`](docs/roadmap.md) §7). Everything the roadmap calls the
unblocked half is done and is listed above;
[`docs/phase-27-verification.md`](docs/phase-27-verification.md) says what each
remaining item is waiting for and on whom.

---

## Phase 28 — Migrating the catalogue

**Exit criteria:** every one of the sixteen Features has a recorded decision —
service, or in-process, with the reason — and the ones that should move have
moved.

Not "convert everything", and the honest split **is** the deliverable. A pivot
that converted the catalogue without writing down why each one moved would leave
the next person guessing, and the in-process path is not a legacy to be
apologised for: it is the only way to be inside the store's transaction.

- [x] **The decision table**, for all sixteen, in
      [`docs/feature-architecture-decisions.md`](docs/feature-architecture-decisions.md).
      Four should be services and one of them is; eight stay in-process and
      eight of those are the transaction argument rather than a preference;
      three are arguable and move only when `analytics-core` does. It also
      records what would revise each decision, so the next argument starts from
      evidence. The original sketch, kept because it is what the table was
      argued against:
      - **Should be services** — anything integrating a third party, anything
        with a vendor credential, anything whose logic is identical for every
        store: `external-marketplaces`, `marketing-automation`, `ai-reports`,
        and `subscriptions` (done)
      - **Should stay in-process** — anything that must be inside the store's
        transaction. There is no way to be inside a transaction over HTTP, and
        `advanced-inventory` reserving stock during checkout is the clearest
        case
      - **Genuinely arguable** — the rest, and the argument gets written down
- [ ] **The ones marked "service" delivered as services**, each with the same
      end-to-end drill step `subscriptions` has
- [ ] **A vendor wired to at least one of them.** Four Features honestly refuse
      without a credential and none has ever called anybody — *needs four
      accounts*. Carried from phases 15 and 17
- [!] **The abandoned-cart event is a design, not a publisher.** It was carried
      as "the catalogue exists, so this is a publisher" — and the reference
      store has **no cart model at all**. Nothing holds a cart long enough to be
      abandoned, so the event would be fabricated. `marketing-automation` needs
      the base store to grow a persisted cart first, which is base-store product
      work and a decision rather than a task
- [x] **Configuration is validated against the manifest.** A setting the
      manifest never declared, one of the wrong type, or a secret the Feature
      will never read is refused with the key named — rather than saved,
      encrypted, and silently doing nothing. Judged against the manifest of the
      version the store actually has; a manifest that cannot be read judges
      nothing, because refusing an operator's change over a fault that is not
      theirs is worse than the typo. Carried from phase 3.5
- [ ] **The orphan identities withdrawn** — `analytics`, `loyalty`,
      `order-management` and the rest, carried from phase 12
- [ ] **Feature and version creation from the dashboard**, carried from phase 6:
      publishing is a command-line act and an operator should not need a
      terminal to withdraw a bad version
- [ ] **Per-feature plan composition and time-boxed prices**, carried from
      phase 6

**Gate: half met.** The decision table exists for all sixteen. The three marked
"service" are not delivered as services and not in the drill — each is a
phase-22-sized piece of work, and moving three in one pass without that scrutiny
each would be the same mistake at three times the size.
[`docs/phase-28-verification.md`](docs/phase-28-verification.md).

---

## Phase 29 — The production gate

**Exit criteria:** all six conditions in [`docs/roadmap.md`](docs/roadmap.md) §2
are true. This is the release decision, and it is the product owner's.

- [ ] **The external security review of the code-delivery path**, and a decision
      recorded against every finding. The scope and briefing pack are ready in
      [`security/external-review-scope.md`](docs/security/external-review-scope.md).
      **Longest lead time of anything on this list** — R16 stays open until the
      report exists
- [ ] **The architecture-validation questions from phase 0**, answered. Eleven
      of them, in [`risks.md`](docs/risks.md) §3
- [ ] **The restore drill against production-shaped data.** It runs in CI on
      every push against a seeded database; it has never run against a real one
- [x] **DNS TXT verification is built** — the half that never was. A TXT lookup
      at `_knight-verification.<domain>`, in about a hundred lines of DNS and no
      new dependency, tried after the HTTP method: HTTP is what an operator can
      satisfy in a minute with a file, DNS is the only one available to a store
      that has no server yet. The answer is compared and never fetched, a record
      that merely contains the token does not verify, and a self-referential
      compression pointer cannot hang the parser. Thirteen tests
- [ ] **Nothing in the delivery path gates on it.** A store can still be
      installed into while its domain is `Pending`.
      `RequireDomainVerification` exists on the handshake and is off by default;
      turning it on stops every store with an unverified domain from handshaking,
      which is a release decision rather than a switch to flip quietly
- [ ] **A decision on the in-process path**: deprecated with a date, or kept
      indefinitely as the transactional option. Phase 28's decision table is the
      input; this is the call

**Gate: yours.** One item on this list was code and is done; the other four are
the security review, eleven answers, a production database and the call on the
in-process path. [`docs/phase-29-verification.md`](docs/phase-29-verification.md)
says what each is waiting for.

---

## Beyond the production gate

Product work on individual Features, deliberately outside the release path.
Recorded here so they are not mistaken for gaps in the architecture, and so
nobody rediscovers them as new.

- **Real-time order updates for the kitchen board** — `restaurant-operations`
  polls; a board on a wall should not
- **Geography in routing** — a `Location` carries latitude and longitude and
  `multi-location` routes by rule rather than by distance
- **A branch's menu joined to its stock** — `multi-location` can say a branch
  serves something and `advanced-inventory` can say it has none
- **Fuzzy matching for `advanced-search` 1.1**, carried since phase 13
- **Tax computation.** Jurisdictional, and wrong is a legal matter rather than a
  bug. The figure is settable on a draft and KNIGHT does not calculate it
- **Frontend quality**: shadcn/ui for the heavier primitives, type generation
  from the OpenAPI document, per-route error boundaries, a logical-property lint
  rule, and a Playwright suite to replace the hand-driven browser walk
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
