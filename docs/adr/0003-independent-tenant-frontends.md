# 0003. Independent Tenant Frontends

## Status

Accepted

## Context

Tenants operate different business types (restaurants, cafés, dessert shops,
bakeries, and similar) with different feature needs and no requirement to
share visual identity or navigation structure. Forcing every tenant through
one fixed storefront/admin template would constrain product design and
couple unrelated tenants' releases together.

## Decision

Each tenant gets its own independently designed and independently deployable
storefront and administration frontend (`frontend/tenants/<slug>/storefront`,
`frontend/tenants/<slug>/admin`), consuming the shared central API. Only frontend
building blocks with a proven cross-tenant need live in `frontend/shared/`.

## Consequences

- Tenant frontends can differ completely in design, feature set, and release
  cadence.
- No tenant's frontend is a template or example for another; nothing
  tenant-specific belongs in `frontend/shared/`.
- The central backend API must remain the single source of truth for
  business rules, feature access, and permissions — frontends never
  duplicate that logic.
- More initial frontend engineering effort per tenant, accepted as the
  tradeoff for design and roadmap independence.
