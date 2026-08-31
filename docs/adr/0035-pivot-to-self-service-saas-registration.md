# 0035 — KNIGHT is a self-service SaaS: public registration is allowed

**Status:** accepted
**Supersedes, for the customer principal only, the "no public registration" rule in
[`architecture/authorization.md`](../architecture/authorization.md) and
[`security/README.md`](../security/README.md).**

## Context

KNIGHT was built as an **agency** control plane: a web-design company signs a
customer, and an operator creates the customer, registers the store, issues a
credential and starts provisioning by hand. Every earlier document said so, and
one said it as a rule — *"there is deliberately no public registration endpoint
for either principal type, and never will be."*

That rule was written to protect two real things, and it still protects them:

- A **platform administrator** must never be publicly registrable. The first and
  every subsequent one is bootstrapped offline with `tools/Knight.Bootstrap`.
- The API must never grow an **account-existence oracle** or an unauthenticated
  path to privilege.

But the product owner's intent has changed: KNIGHT is to be a **self-service
SaaS**. A merchant who discovers it should be able to register, choose a plan,
pay KNIGHT, and receive a working store — with no operator in the loop. Keeping
manual admin creation as a required onboarding step is incompatible with that.

The earlier document already anticipated this: it called an end-customer identity
"an intentionally separate concept … [that] will get its own module," and the
provisioning pipeline's manual steps carry the comment *"only who completes it
changes when the automation lands."* The automation is now landing.

## Decision

1. **Public registration is a first-class feature** for the **customer**
   principal (the merchant who owns stores). `POST /api/v1/auth/register` and
   email verification exist and are rate-limited, abuse-protected, and free of an
   account-existence oracle.
2. **Platform administrators remain non-registrable.** `Knight.Bootstrap` stays
   the only way one comes into being. The "never" in the old rule holds for
   `PlatformAdmin` and is unchanged.
3. **Normal onboarding requires zero manual operator intervention.** Registration
   → payment → subscription → entitlements → store → provisioning → agent →
   feature delivery → activation is automatic. Operators intervene only on
   genuine failure (retry/resume/suspend/grant), and every such action is
   audited.
4. **Two billing domains stay separate forever.** `knight_billing`
   (merchant → KNIGHT) is distinct from `store_payments` (end customer →
   merchant). The self-service billing system never touches a store's own payment
   provider.
5. **The control plane stays the source of truth.** The agent is an execution
   mechanism that receives the effective desired state; it never decides what a
   customer is entitled to. The existing feature-delivery engine is reused — no
   second installer.

The full plan is [`self-service-saas-plan.md`](../self-service-saas-plan.md).

## Consequences

- The operations dashboard remains necessary — for support, fraud review,
  billing and provisioning intervention, and customer/feature management — but is
  no longer on the normal onboarding path.
- `Subscription` gains a `Pending` (awaiting-payment) state; a paid subscription
  is activated **only** by a signature-verified, idempotent provider webhook,
  never by a browser redirect.
- The three `Manual` provisioning steps (`server`, `instance`, `domain-tls`)
  become `Automatic` behind an infrastructure adapter. A simulated adapter runs
  the whole journey locally; the real one waits on the hosting-platform decision
  that is still the product owner's ([`roadmap.md`](../roadmap.md) §7).
- Billing state and provisioning state are two independent state machines: a
  failed provisioning never marks a paid subscription failed, and a past-due
  subscription can sit over a ready store.
- The security posture is unchanged where it mattered: no admin self-registration,
  no existence oracle, object-level tenant isolation on every customer/store
  resource, store-scoped agent credentials, and audited administrative overrides.
