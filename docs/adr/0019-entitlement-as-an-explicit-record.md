# ADR 0019 — Entitlement as an explicit record, reconciled from the subscription

Status: **accepted** · Date: 2026-08-18 · Phase: 2

## Context

Phase 2 has to answer one question reliably: **what has this customer paid for?**
Everything downstream leans on it — delivery decides what to install
([`0014`](0014-features-as-deployable-packages.md)), ingestion decides what a
store may do, and billing decides what to charge.

There were two ways to answer it:

1. **Derive it on demand** — walk the subscription and its plan every time
   someone asks.
2. **Record it** — store a `FeatureEntitlement` row per customer per feature, and
   keep those rows in step with the subscription.

Deriving is tempting because it cannot drift. But it makes three things
impossible to express: a capability granted outside any plan (a pilot, goodwill,
a migration promise), a time-boxed grant, and the history of what a customer held
last March when an invoice was issued.

## Decision

**Entitlement is an explicit record, and it is reconciled from the
subscription.**

- `FeatureEntitlement` carries `source` (Plan | Optional | Grant), when it was
  granted, an optional expiry, and its revocation with a reason.
- `ReconcileAsync` is the only thing that grants or revokes plan-derived
  entitlements. It is idempotent, and every mutating subscription path ends in
  it, so entitlements never describe a subscription that has since moved on.
- **Manual grants are outside reconciliation's remit.** They were made
  deliberately against the plan; a plan change is not a decision to withdraw one.
  Only an explicit revocation removes them.
- Granting and revoking raise `FeatureEntitlementGranted` /
  `FeatureEntitlementRevoked`, and both are audited — including the ones
  reconciliation makes on the customer's behalf.

## Consequences

**Bought**

- The three ways a customer can hold a capability are distinguishable, and each
  is attributable.
- "What did they hold when this invoice was issued?" is answerable, because
  revocation keeps the row.
- Phase 3.5 has a real event to hang delivery off, raised at exactly the moments
  a capability starts or stops being owed.

**Paid**

- Reconciliation must be correct, and it is now the single point of failure for
  entitlement drift. It is idempotent and unit-tested against plan changes,
  suspension, cancellation, withdrawn features and infrastructure refusals.
- A row can go stale if a future path forgets to reconcile. Every current path
  does, and the integration suite checks the outcomes rather than the calls.

## Related decisions taken with it

- **An entitlement is still not an installation.** Revocation means *disable* the
  installed feature, never uninstall it and never delete its data
  ([`0016`](0016-feature-migration-and-removal-policy.md)). The published event
  says `Revoked`, and the consumer's contract is to disable.
- **Prices are time-boxed, not overwritten.** Repricing closes the old row and
  opens a new one from the same instant, so an invoice issued last month stays
  explicable from the prices in force last month.
- **One calculator prices everything.** The quote a customer is shown and the
  lines on their invoice come from the same code path, so they cannot disagree.
  A selected feature with no price in force is refused rather than quoted as
  free — free is a decision somebody has to have made.
- **Plans are seed data**, loaded from a JSON catalogue shipped with the
  deployment and editable through the API. Nothing branches on
  `plan.Key == "professional"`.
- **Billing records facts; it does not move money** (`risks.md` R14, resolved as
  invoicing-only). Issued invoices are frozen — correcting one means voiding and
  reissuing — and invoice numbers come from a counter incremented atomically,
  because accounting expects them gapless and a read-then-write cannot promise
  that.
- **Issuing an invoice does not roll the billing period forward.** Deciding *when*
  to invoice is scheduled work, and phase 2 deliberately leaves the two
  decisions separable rather than hiding one inside the other.
