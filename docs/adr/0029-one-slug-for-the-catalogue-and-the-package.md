# 0029 — One slug for the commercial catalogue and the deployable package

- Status: **Accepted**
- Date: 2026-08-25

## Context

KNIGHT has two registries that both name features, and until now they named
them differently.

The **commercial catalogue** — seeded from
[`commercial-catalogue.json`](../../backend/src/Knight.Infrastructure/ControlPlane/Seed/commercial-catalogue.json)
— held `analytics`, `loyalty`, `log-shipping`, `ai-recommendations`. The
**package registry** held what `features/` actually builds:
`knight-feature-analytics-core`, `knight-feature-analytics-reports`,
`knight-feature-promotions`, `knight-feature-delivery`.

The two sets did not overlap at all.

This was not cosmetic. `FeatureVersionService` resolves a published version by
the slug in the manifest:

```csharp
?? throw new NotFoundException($"No feature is registered with slug '{manifest.Slug}'.");
```

So publishing any of the four real packages against a freshly seeded KNIGHT
failed. Every part of the delivery engine worked — registry, resolver, jobs,
agent, migrations, rollback — and nothing could be delivered, because the thing
being sold and the thing being shipped had no name in common. The gap was
invisible in tests, because the unit suites construct their own `Feature`
objects and the integration suites publish against features they seed
themselves.

Three ways to close it:

1. **One slug.** The catalogue is seeded with the slug the package declares.
2. **A `packageSlug` field** on `Feature`, mapping a commercial identity to a
   deliverable one.
3. **One Feature, many packages** — a sold capability installs a set of
   packages.

## Decision

**One slug.** A Feature's slug is the slug its package declares, and there is
no mapping between the two.

This is what the code already assumed. `FeatureSlug` says so in as many words:

> A feature's slug is the name its Python package carries, so it is normalised
> to exactly what a package name may be.

The value is the short, product-facing form — `analytics-core`, not
`knight-feature-analytics-core`. The manifest's `slug` and the catalogue entry
carry it identically. The Python distribution and module names keep their
`knight-feature-` / `knight_feature_` prefix: those are PyPI namespace
concerns, and `knight_package.py` already derives the artifact name from the
manifest's slug and the source directory from `django.installed_app`, so the
two were never coupled.

A capability with no package — `log-shipping` is the only one, enforced by
KNIGHT's own ingestion endpoint rather than by store code — is still a Feature
with a slug, and still not a flag: it is entitled, refused when unentitled, and
audited. It simply has no artifact to install.

## Consequences

**Positive** — the failure above is impossible: an entitlement cannot name a
capability the registry cannot resolve, because there is only one name. A
reader of an invoice, a plan, a manifest, an audit entry and an install job
sees the same string, which is what makes those five things traceable to each
other at all.

**Negative** — the commercial name and the engineering name can no longer
diverge. Marketing cannot rename a capability without renaming its package, and
renaming a published slug is a breaking change: a store that installed
`analytics-core` has that string in its local registry, its migration table
lineage and its audit history. The mitigation is that the display **name** and
description are free text and are what a customer actually reads; only the slug
is fixed.

**Also** — this decision was cheap to take now and would not have been later.
Nothing is in production, the four affected packages were installed only in
local development with `sha256:local-development` digests, and no customer has
been billed against an old slug. The same change after the first paying
customer would have needed a migration of entitlements, installations, invoice
lines and audit records.

**Operationally** — seeding is additive and never deletes, so a deployment
already seeded from the old file keeps `analytics`, `loyalty`,
`order-management` and `ai-recommendations` as orphan identities. They are not
referenced by the new plans, and retiring them is a withdrawal through the API,
not an edit to the seed file.
