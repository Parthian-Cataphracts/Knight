# ADR 0037 — Composed pricing: a Feature made of sub-features

**Status:** Accepted (foundation), 2026-09-05. Superseding parts require the
product owner's sub-feature list and prices (Phase 33).

## Context

Phase 33 introduces the first sellable Feature whose price is not one line.
"Automatic Admin" generates and publishes marketing content across channels, and
it is composed of independently-sellable parts — an Instagram post, a Divar post,
a Basalam post, image generation, caption generation, a story. The customer ticks
the parts they want; the price is the sum of what they ticked; and they must see,
and be entitled to, only the parts they bought.

The catalogue today prices a Feature as a single line. The question this ADR
answers is: **how does a Feature carry sub-priced parts** without inventing a
second pricing, entitlement, delivery and UI-gating stack beside the one that
already works?

## Decision

**A sub-feature is a Feature.** It is grouped under a parent by a nullable
`Feature.ParentFeatureId`; everything else about it is an ordinary Feature.

The consequences fall out of the machinery that already exists:

- **Pricing composes for free.** The pricing calculator already prices a plan as
  its base plus a line for every *selected* Feature, each at its own
  time-scoped `FeaturePrice`. A composed Feature's price for a customer is
  therefore the sum of the sub-features they selected — the same computation,
  one level of grouping deeper. No new pricing model, no per-Feature price table
  change.
- **Entitlement composes for free.** An entitlement is per-Feature. Buying a
  sub-feature entitles that sub-feature; the parent is the group, not a single
  grant. "Composed of sub-entitlements" is just "several Feature entitlements
  that share a parent".
- **Phase-32B gating composes for free.** Visible UI mounts are already computed
  per-Feature from installed + enabled + entitled. A customer sees the mount of
  each sub-feature they bought and no others, with no new gating rule.
- **Delivery composes for free.** Each sub-feature is a versioned, deployable
  Feature (or an `external_service` one) delivered by the existing engine.

The only genuinely new fact is the **parent grouping**, which lets the catalogue
and the customer portal *present* a composed Feature as one page that totals a
selection — a read/display concern, not a new commercial primitive.

### Rules

- The grouping is set only while the sub-feature is a **Draft**. Moving a
  Feature into or out of a group after it has been sold would silently change
  what a customer's selection totals to and what their entitlement composes
  from.
- A Feature cannot be its own parent (enforced in the aggregate).
- Composition is **one level deep**: a parent is not itself a sub-feature. That a
  parent exists and is top-level is checked in the service, which can read the
  other row; the aggregate enforces only what it can see locally.
- No database foreign key on `ParentFeatureId`: a Feature is never hard-deleted
  (it is withdrawn), so the rule that matters lives in the service, and a
  self-referencing FK would complicate seed ordering. An index supports "the
  sub-features of this parent".

## What this ADR does *not* decide

These are the product owner's, and gate the rest of Phase 33 — not this
foundation:

- The **sub-feature list** and **per-item prices** (the owner supplies them; the
  seed uses the brief's set with placeholder prices).
- The **AI provider** for generation (model and key), behind a seam like
  `IPlatformPaymentProvider`.
- Each **channel integration** (Instagram, Divar, Basalam) — its API,
  credentials and authorisation flow, several owner-supplied.
- Any **bundle rules** (a discount for taking the whole set) beyond the plain
  sum; the sum is the default and the only rule until the owner defines one.

## Consequences

Positive: the whole of pricing, entitlement, delivery and UI gating is reused;
composition is a grouping and a display, not a parallel stack. Negative: a
"Feature" now spans two meanings a reader must hold — a top-level product and a
priced part of one — which the `IsSubFeature` predicate and this ADR name
explicitly.
