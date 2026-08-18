# 0004. Feature-Based Tenant Capabilities

## Status

Accepted

## Context

Not every tenant needs every capability — a bakery may not need table
reservations, a dine-in restaurant may not need delivery. Business modules
must be able to declare capabilities that are optional per tenant, checked
server-side, and independent from per-user permissions.

## Decision

Introduce `FeatureManagement` as its own module: a `FeatureDefinition` (the
capability, identified by a stable key) and a `TenantFeature` (whether a given
tenant has that capability enabled). `IFeatureAccessService` is the single
server-side authority modules and the API must consult before executing
feature-gated behavior. This is deliberately separate from permissions
(`docs/architecture/authorization.md`), which govern per-user actions within
an already-enabled feature.

## Consequences

- New business modules declare features without changing `Tenancy` or
  `Identity`.
- Feature checks are server-enforced; no frontend is trusted to gate access.
- Tenant onboarding/configuration becomes a matter of toggling
  `TenantFeature` rows rather than deploying tenant-specific code.
- Feature flags are a tenant-wide switch, not a percentage rollout or
  experimentation mechanism — a different concern if it's ever needed.
