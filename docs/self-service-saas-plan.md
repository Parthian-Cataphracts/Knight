# Self-Service SaaS — implementation plan

> **Status:** planned, not yet built. This document is the authoritative plan for
> the transition from the **agency model** (an operator creates customers and
> stores by hand) to a **self-service SaaS model** (an anonymous visitor
> registers, pays KNIGHT, and gets a fully provisioned, correctly entitled store
> with **zero manual operator steps**).
>
> The decision to allow public registration overrules the earlier "no public
> registration" rule **for the customer principal only** — see
> [`adr/0035`](adr/0035-pivot-to-self-service-saas-registration.md). Platform
> administrators are still bootstrapped offline and are never publicly
> registrable.

Read alongside [`store-provisioning.md`](store-provisioning.md),
[`feature-delivery.md`](feature-delivery.md), [`authorization.md`](authorization.md)
and [`domain-model.md`](domain-model.md).

---

## 1. The one sentence that matters

A customer who discovers KNIGHT today must go from anonymous visitor to a fully
operational, correctly entitled, independently accessible store **without a human
administrator performing any provisioning step**. The only acceptable manual
involvement is an exceptional operational intervention after a genuine failure.

The target lifecycle:

```
Public registration → Customer created → Email verified → Plan selected
  → Platform checkout → Payment (provider webhook) → Subscription activated
  → Entitlements resolved → Store record created → Provisioning job started
  → Infrastructure provisioned → Agent bootstrapped → Agent connects
  → Features installed → Health check → Store READY → Access email → Customer logs in
```

Every stage is **asynchronous, idempotent, observable, retryable and recoverable.**

---

## 2. Audit of what already exists — reuse, do not rebuild

The good news from reading the repository: most of the target domain is already
modelled. The self-service transition is mostly **new front doors** (public
registration, a self-service billing provider, a customer portal) plus **one new
wire** (payment confirmation automatically starting provisioning) — not a new
back end.

| Target concept (spec) | Already in the repo | Where | Verdict |
|---|---|---|---|
| Customer | `Customer` aggregate with status lifecycle | `backend/modules/Customers/Domain/Customer.cs` | **Reuse.** Add `Pending` self-signup origin. |
| Store + lifecycle | `Store`, `StoreCredential`, `StoreHandshake`, `StoreDeployment`, `StoreEnums`, health | `backend/modules/Stores/Domain/*` | **Reuse.** |
| Plan / pricing / included features | `Plan`, `PlanFeature`, `FeaturePrice`, `PricingCalculator`, `PlanService` | `backend/modules/Plans/*` | **Reuse.** Add a public projection + `publiclyPurchasable`. |
| Subscription state machine | `Subscription` (Trial/Active/PastDue/Suspended/Cancelled), `SubscriptionFeature` | `backend/modules/Subscriptions/Domain/Subscription.cs` | **Reuse + extend** with a `Pending` (awaiting-payment) state. |
| Entitlements | `FeatureEntitlement`, `EntitlementService` | `backend/modules/Subscriptions/*` | **Reuse.** This is the desired-state source of truth. |
| Provisioning job + state machine | `ProvisioningJob`, `ProvisioningPipeline`, `ProvisioningStepResult`, `ProvisioningService.StartProvisioningAsync(storeId, idempotencyKey)` | `backend/modules/Provisioning/*` | **Reuse.** The step list is already `server → instance → store-record → credentials → agent → base-features → configuration → domain-tls → healthcheck`. |
| Secure agent bootstrap | `Agent.Provision` issues a **one-time token**, enrolment consumes it and sets a **long-lived credential**, `AuthenticateAsync` fixed-time-compares, revocation exists; the credential is scoped to one machine | `backend/modules/Servers/Domain/Agent.cs`, `AgentService.cs` | **Reuse.** This is exactly the store-scoped, short-lived-bootstrap → rotated-credential model the spec asks for. |
| Feature delivery engine | `FeatureDeliveryService`, `AgentJobService`, `FeatureInstallation(Job)`, `FeatureRollout`, dependency resolution | `backend/modules/FeatureDelivery/*`, `backend/modules/FeatureRegistry/*` | **Reuse. Do not build a second installer.** |
| Dependency resolution (CUSTOM validation) | `DependencyResolver`, `ResolutionModel`, `FeaturePlanResolver` | `backend/modules/FeatureRegistry/Domain/*` | **Reuse** for pre-checkout validation. |
| Roles / permissions / isolation | `CustomerOwner`, `CustomerStaff`, `ICustomerOwned`, customer scope filter | `backend/modules/AccessControl/*` | **Reuse.** Grant `CustomerOwner` the missing self-service permissions. |
| Invitation / activation | `/api/v1/auth/activate` | `backend/src/Knight.Api/ControlPlane/ControlPlaneAuthEndpoints.cs` | **Reuse** the token/email plumbing for verification + access email. |
| Outbound email | account-invitation sender + transactional email (phase 9) | `AccessControl.IAccountInvitationSender`, mail infra | **Reuse** for the new transactional events. |
| Observability / audit / incidents | correlation, audit trail, error/incident pipeline | `backend/modules/Observability/*`, `AuditTrail` | **Reuse** — thread the journey correlation id through. |

### What is genuinely missing (the actual work)

1. **Public registration + email verification** front door (auth).
2. **`knight_billing`** — a self-service platform-billing domain: a provider
   abstraction, a checkout session, an authoritative price calculation, and a
   **signature-verified, idempotent webhook** that is the *only* thing that
   activates a paid subscription. Distinct from the existing agency `Billing`
   (invoices/payments) and from any store's own payment gateway.
3. **The automatic wire**: webhook → activate subscription → resolve entitlements
   → create the `Store` record → `StartProvisioningAsync` — with no operator in
   the loop. Today provisioning is started by an operator endpoint.
4. **Making the three `Manual` provisioning steps automatic** (`server`,
   `instance`, `domain-tls`) behind an **infrastructure adapter**, with a
   dev/simulated adapter so the flow runs end-to-end locally. The real adapter
   depends on the still-open hosting-platform decision (see §11).
5. **Customer self-service API** (`/api/v1/me/*`) and **customer portal** screens,
   kept separate from the operations portal.
6. **Public plan catalog** + **CUSTOM feature selector** with dependency
   validation before checkout.
7. **Lifecycle policy**: billing state vs provisioning state kept as two separate
   machines; grace/suspension/cancellation with retention (no destructive delete
   on payment failure or cancellation).

---

## 3. The two billing domains — never merged

```
knight_billing          Merchant → KNIGHT        (BASIC / CUSTOM / PROFESSIONAL)
  ↳ subscription, platform billing transaction, invoice, entitlement

store_payments          End customer → Merchant  (the store's own Stripe/PayPal/…)
  ↳ lives inside each Django store; KNIGHT never reads or writes it
```

These use separate models, services, permissions, webhook routes and transaction
concepts. The self-service billing system must never touch a merchant's store
payment configuration, and a store's payment provider is never KNIGHT's concern.
The existing agency `Billing` module (invoice-centric) is folded into / sits
beside `knight_billing`; the customer-facing checkout is new.

---

## 4. Domain changes, precisely

Additions/edits, aggregate by aggregate. Everything else is reused unchanged.

- **Customer** — add a creation path `RegisterSelfService(...)` producing status
  `Pending` until email is verified, then `Active`. Keep the existing
  operator-created path.
- **ControlPlaneUser** — a self-registered user is a `CustomerUser` in the
  `CustomerOwner` role, created unverified. Add an `EmailVerified` fact and a
  verification token (reuse the activation-token mechanism).
- **Plan** — add `IsPubliclyPurchasable`; a public projection that omits internal
  metadata. `PricingCalculator` already computes authoritative totals — the
  server always recomputes; a client-supplied amount is never trusted.
- **Subscription** — add a **`Pending`** status (created at checkout, before
  payment) and `Activate` from `Pending`. Record `provider`,
  `providerSubscriptionId`, and `cancelAtPeriodEnd`. Keep the existing terminal
  and past-due transitions. Billing state stays **independent** of provisioning
  state.
- **New: `PlatformBillingTransaction`** (in `knight_billing`) —
  `customerId, subscriptionId, provider, providerTransactionId, amount, currency,
  status {Pending,Succeeded,Failed,Refunded,PartiallyRefunded}, idempotencyKey`.
- **New: `CheckoutSession`** — ties a plan + selected features + interval to a
  provider session id and the authoritative computed price.
- **Entitlement** — already source/effective-window aware via `FeatureEntitlement`;
  ensure `source ∈ {Plan, CustomPurchase, Promotion, AdminGrant}` and that
  resolution yields the **effective desired state** the delivery engine consumes.
- **ProvisioningJob** — reuse. Ensure it carries the **journey correlation id**
  and that each step is independently retryable with `attemptCount`/`lastError`
  (fields already exist; confirm classification `Transient | Permanent |
  ManualIntervention`).
- **Store** — reuse. The provisioning state machine is the `ProvisioningPipeline`
  steps, not a boolean.

---

## 5. The provisioning state machine (already present)

`ProvisioningPipeline.StepsFor(Provision)` is, in order:

```
server → instance → store-record → credentials → agent
  → base-features → configuration → domain-tls → healthcheck → (Store Active)
```

Today `server`, `instance` and `domain-tls` are `Manual`; the code comment
already anticipates the automation: *"only who completes it changes when the
automation lands."* The self-service work **flips those three to `Automatic`**
behind an `IInfrastructureAdapter`, and leaves the rest as-is. Failure handling,
retry, backoff, dead-letter and the `Transient/Permanent/ManualIntervention`
classification are added at the orchestrator, never inside an HTTP request.
Infrastructure is **not** destroyed on every failure.

Customer-facing status is a **friendly projection** of the internal step
(`infrastructure_provisioning → "Preparing your store"`,
`features_installing → "Installing your selected features"`,
`health_checking → "Finalizing your store"`).

---

## 6. The public + customer API surface

Naming follows existing conventions (`/api/v1/...`, problem+json, stable error
codes). Customer endpoints resolve ownership from the **authenticated principal**,
never from a client-supplied `customerId`/`storeId`.

**Auth (public):**
- `POST /api/v1/auth/register` → `{email, password, name, companyName?}`; rate
  limited; does **not** reveal whether an email already exists.
- `POST /api/v1/auth/verify-email`, `POST /api/v1/auth/resend-verification`
  (throttled).

**Catalog (public):**
- `GET /api/v1/plans` — publicly purchasable plans, with price, interval,
  included capabilities, limits, feature summaries.

**Billing (`knight_billing`):**
- `POST /api/v1/billing/checkout` → `{planId, billingInterval, selectedFeatureIds[]?}`;
  server computes the authoritative price and validates CUSTOM dependencies;
  returns `{checkoutUrl, checkoutSessionId}`.
- `POST /api/v1/billing/webhooks/{provider}` — signature-verified, idempotent,
  replay-resistant. **The only** activation path.

**Customer self-service (`/me`, authenticated CustomerOwner):**
- `GET /api/v1/me/subscription`, `POST /api/v1/me/subscription/cancel`
  (`cancelAtPeriodEnd=true` by default).
- `GET /api/v1/me/stores`, `GET /api/v1/me/stores/{id}`,
  `GET /api/v1/me/stores/{id}/provisioning` (friendly step + progress).
- (Optional) `POST /api/v1/me/stores` — preferred: the backend creates the first
  store automatically after confirmed payment; the frontend never drives
  provisioning.

**Operations (admin, separate surface, audited):**
- `.../admin/customers` (+ suspend/restore), `.../admin/provisioning`
  (+ retry/resume), `.../admin/subscriptions`, `.../admin/stores/{id}/entitlements`
  (grant/revoke). Some already exist; grants/overrides are audited with
  `admin, action, reason, previousState, newState`.

Stable error codes: `PLAN_UNAVAILABLE`, `INVALID_FEATURE_SELECTION`,
`PAYMENT_REQUIRED`, `PROVISIONING_FAILED`, `STORE_NOT_READY`,
`UNAUTHORIZED_STORE_ACCESS`. No stack traces leave the API.

---

## 7. The automatic orchestration (the heart of it)

```
POST /billing/webhooks/{provider}
  → verify signature
  → find CheckoutSession + PlatformBillingTransaction (by provider ids)
  → idempotency check (return prior result if replay)
  → [tx] mark transaction Succeeded, Subscription Pending → Active
  → [tx] resolve entitlements from Plan (+ purchased features + dependencies)
  → [tx] create Store record (SharedManaged for BASIC/CUSTOM; Dedicated for PRO)
  → [tx] create ProvisioningJob(storeId, idempotencyKey = subscriptionId)
  → enqueue provisioning
                    │
        Provisioning worker (out of band, short transactions per step)
                    │
  server → instance (IInfrastructureAdapter, idempotent: detect-then-create)
  → store-record → credentials → agent (one-time token issued)
  → agent enrols (consumes token, gets long-lived credential)
  → configuration (handshake) → base-features (FeatureDeliveryService,
        desired state = resolved entitlements) → domain-tls → healthcheck
  → Store Active → STORE_READY email (short-lived, single-use admin link)
```

Rules that keep it correct:
- **No infrastructure work inside the HTTP request**; the request only commits a
  job and returns.
- **Short transactions** around each state transition; never hold a DB
  transaction open across an external call (provider, infra, agent, download,
  migration).
- **The control plane is the authority**; the agent receives the effective
  desired state and never decides entitlements.
- **Reuse the delivery engine** for install/upgrade/dependency/migration/report.
- **Billing state ≠ provisioning state.** A `FAILED` provisioning never flips a
  paid `Active` subscription to failed; a `PastDue` subscription can sit over a
  `READY` store.

---

## 8. Idempotency & failure handling (non-negotiable)

- Registration, webhook, provisioning job, agent registration and feature install
  are each idempotent (natural keys: email, provider ids, `subscriptionId`,
  agent identity, installed version).
- Every step declares `success | retryable | permanent | manual-intervention`.
  Retries use max-attempts + exponential backoff + a dead-letter/failed state +
  operational alerting. **Never retry forever.**
- No destructive action on failure: infra is not torn down, features are not
  deleted, customer data is retained per policy.

## 9. Suspension & cancellation lifecycle

```
Billing:   Active → PastDue → Grace → Suspended        (recoverable after payment)
Cancel:    CancelRequested → ActiveUntilPeriodEnd → Expired
```

Suspension follows commercial policy; admin access may remain; features are not
silently deleted; infrastructure enters a retention window rather than immediate
destruction. Destructive deletion is an explicit, separate lifecycle policy.

## 10. Security, isolation, observability

- Public surface: rate limiting, password policy, email verification, abuse/dup
  protection, no account-existence oracle.
- Webhooks: signature verification, idempotency, replay resistance, audit.
- Every customer/store resource: object-level authorization; a customer can never
  reach another customer's store, subscription, transaction, entitlement, agent
  or provisioning job. Ownership is resolved from the principal.
- Agent: store-scoped identity, one-time bootstrap, rotation, revocation, audit,
  replay protection — already the model in `Agent`.
- Secrets: encrypted at rest, never in logs, never in email.
- One **journey correlation id** threads registration → payment → subscription →
  provisioning → agent → delivery → activation. Metrics: registration/payment/
  provisioning success rates, average provisioning time, feature-install and
  agent-connect failure rates, retry counts, failed jobs.

## 11. Known external dependencies (product-owner decisions)

These gate the *real* automation but not the *end-to-end shape*, which the
dev/simulated adapters exercise fully:
- **Payment provider** choice (drives the `knight_billing` provider adapter and
  webhook signature scheme).
- **Hosting platform** choice (drives the real `IInfrastructureAdapter` for
  `server`/`instance`/`domain-tls`). Until then, a simulated adapter marks those
  steps complete so the full journey — and its tests — run locally. This is the
  same open decision already tracked in [`roadmap.md`](roadmap.md) §7.

---

## 12. Implementation order

Grounded restatement of the spec's phases; each phase ends with real tests.

- **Phase A — domain foundation.** `Subscription.Pending`; `Plan.IsPubliclyPurchasable`
  + public projection; `PlatformBillingTransaction` + `CheckoutSession`
  (`knight_billing`); confirm `ProvisioningJob` retry/classification fields;
  `CustomerOwner` permission grants. Migrations. Unit tests for every new
  transition.
- **Phase B — public auth.** `register`, `verify-email`, `resend-verification`;
  self-service `Customer`/user creation; tenant isolation tests; the
  account-existence-oracle test.
- **Phase C — billing.** public `GET /plans`; CUSTOM selection + dependency
  validation (reuse `DependencyResolver`); `POST /billing/checkout` with
  authoritative pricing; provider webhook (signature, idempotency, replay);
  subscription activation + entitlement generation.
- **Phase D — provisioning automation.** `IInfrastructureAdapter` (+ simulated
  dev adapter) flipping `server`/`instance`/`domain-tls` to automatic; the
  webhook→job wire; orchestrator with retry/backoff/dead-letter; health checks.
- **Phase E — feature delivery integration.** resolve desired state from
  entitlements; invoke the existing delivery engine; dependencies; verify
  installed state; report status. (No second installer.)
- **Phase F — customer portal.** signup → verify → plan select → CUSTOM selector
  → checkout → **provisioning progress** → store-ready → billing/feature
  management. Separate route tree, role-gated, from the operations portal.
- **Phase G — operations.** provisioning dashboard with retry/resume, entitlement
  grant/revoke, suspend/restore, agent monitoring, audit — extending the existing
  admin screens.

---

## 13. Definition of Done

Not done until a brand-new anonymous visitor can, with **zero manual admin
intervention**:

```
register → verify email → choose BASIC/CUSTOM/PROFESSIONAL → select optional
features → pay KNIGHT → receive confirmation → watch provisioning run → store
infrastructure created → agent securely bootstrapped → agent connects →
entitlements resolved → features installed → health check passes → store READY →
admin access email → open store admin → see active features
```

### The one acceptance test that proves it

An end-to-end integration test that runs the full journey against the simulated
infrastructure adapter and a fake payment provider: register → verify → select
CUSTOM(Reviews + Analytics) → dependency validation → checkout → simulate payment
→ webhook → activate → entitlements → store → provisioning job → infra → agent
bootstrap → agent authenticates → resolve entitlements → install Reviews +
Analytics → health check → READY → access email — **with no operator step.** This
test is the definition of the feature; the phase is not complete without it green
alongside the existing suites.
