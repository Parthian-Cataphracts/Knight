# 0014 — Features are versioned deployable Django packages, not flags

- Status: **Accepted**
- Date: 2026-08-18
- Amends: the feature model in `domain-model.md`, `architecture.md`,
  `store-integration.md`, and the entitlement wording in `authorization.md`

## Context

The first architecture revision modelled a Feature as an entitlement flag:
KNIGHT decided `advanced_analytics = true` and the store enforced it. That is
only half the model and it silently assumes the functionality already exists in
every store — which would mean a developer implementing and copying each
capability into every customer's Django application by hand.

The actual business requirement is the opposite: a capability is developed
**once** as a reusable artifact, versioned centrally, and delivered
automatically to any store whose customer buys it.

## Options considered

1. **Flag only** (previous revision) — requires manual per-customer development
   and per-store code duplication. Rejected: it contradicts the core business
   requirement and does not scale past a handful of stores.
2. **Fork a store template per customer** — every customer gets a copy of the
   code, patched by hand. Rejected: N divergent codebases, no upgrade path.
3. **Features as network microservices** — one service per capability, shared
   across stores. Rejected: turns one store into twenty services, breaks store
   independence, and adds operational cost with no matching requirement.
4. **Features as installable Django packages** delivered by KNIGHT.

## Decision

**Option 4.** A Feature is a normal, installable Django app published as a
signed, immutable, semver'd package. KNIGHT holds a **Feature Registry**
(`Feature` + `FeatureVersion` + manifest) and a **delivery pipeline** that
installs a specific version into a specific store.

Entitlement and installation become two distinct, separately tracked concepts
(`feature-delivery.md` §2), with a formal installation state machine (§6).

A Feature Package is explicitly **not** a microservice. It runs inside the
store's own Django process.

## Consequences

**Positive** — one implementation per capability; central version control;
customers get upgrades without bespoke work; the "paid but broken" state
becomes visible instead of invisible; the Professional plan differs only in
infrastructure, not in how software reaches it.

**Negative** — KNIGHT now delivers executable code, which is a materially
higher-risk operation (see [`0015`](0015-feature-delivery-mechanism.md) and
`security-threat-model.md`); Django migrations must be managed remotely with a
rollback story ([`0016`](0016-feature-migration-and-removal-policy.md));
compatibility between store versions and feature versions must be modelled
([`0017`](0017-feature-compatibility-and-dependencies.md)); the agent grows
from a monitor into a lifecycle participant; project scope grows by roughly one
full phase.

**Unchanged** — .NET control plane, React dashboard, PostgreSQL, independent
Django stores with their own databases, customer isolation, monitoring, error
reporting, audit, API-first design, and the no-premature-microservices rule.
