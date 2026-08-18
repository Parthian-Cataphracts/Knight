> **LEGACY DOCUMENT.** This describes the previous product (a shared
> multi-tenant food-service SaaS), not KNIGHT's target control-plane
> architecture. See [`docs/README.md`](../README.md) and
> [`docs/adr/0010`](../adr/0010-pivot-to-control-plane.md). Kept because it
> documents code that still exists in `backend/`.

# Platform Overview

## What this is

A centralized, brand-neutral SaaS platform serving multiple independently
branded tenants (restaurants, cafés, dessert shops, bakeries, and similar food
service businesses). One shared backend serves every tenant; each tenant gets
its own independently designed storefront, admin interface, domain, and set of
enabled features.

```
                        SAAS PLATFORM
                             |
                  Central ASP.NET Core API
                             |
                   Shared Infrastructure
                             |
       +---------------------+---------------------+
       |                     |                     |
     Tenant A              Tenant B              Tenant C
       |                     |                     |
 Custom Storefront      Custom Storefront     Custom Storefront
 Custom Admin           Custom Admin          Custom Admin
 Custom Domain          Custom Domain         Custom Domain
```

## Architectural style

**Modular monolith.** A single deployable ASP.NET Core API composed of
independent modules (`Identity`, `Tenancy`, `FeatureManagement`, and future
business modules such as `Catalog` or `Ordering`). Modules depend on
`Knight.Domain` and `Knight.Application` but never on each other or on
`Knight.Infrastructure` — see `repository-structure.md` for the exact
dependency rules and `docs/adr/0001-modular-monolith.md` for the reasoning.
The boundary is deliberately kept clean enough that a module could later be
extracted into its own service without a rewrite.

## Two operating contexts

The platform distinguishes two fundamentally different contexts:

- **Platform context** — the Platform Super Admin operating across tenants.
- **Tenant context** — a tenant user operating inside exactly one tenant.

These are never inferred from each other. See `authorization.md`.

## Core technology

| Concern | Choice |
|---|---|
| Backend | .NET 10 / ASP.NET Core / C# |
| Database | PostgreSQL via Entity Framework Core |
| Cache | Redis |
| Object storage | Abstracted; S3-compatible in production |
| Local infra | Docker Compose (`infrastructure/docker`) |
| Frontends | Reserved for Next.js/TypeScript — not part of this phase |

## Request pipeline order

```
CorrelationId -> ExceptionHandling -> (dev: OpenAPI/Scalar) -> CORS
   -> RateLimiter -> Authentication -> TenantResolution -> Authorization
   -> Endpoints
```

Correlation and exception handling wrap everything so every response —
including ones short-circuited further down the pipeline — carries a
correlation id and a consistent Problem Details shape. Authentication runs
before tenant resolution because resolution reads validated claims off the
request's `ClaimsPrincipal` (a tenant token's `tenant_id` claim, a platform
admin's `principal_type` claim). Tenant resolution runs before authorization
so both ASP.NET Core authorization policies and endpoint handlers can rely on
`ITenantContext` already being populated — or deliberately left empty — by
the time they run. Health endpoints are exempt from tenant resolution (see
`docs/architecture/multi-tenancy.md`) so liveness/readiness checks never
depend on the database being reachable in a tenant-resolvable state.

## Rate limiting

A minimal, named-policy foundation (`Microsoft.AspNetCore.RateLimiting`) is
in place so platform, authentication, and tenant-public traffic can be tuned
independently later: `platform` (applied to `/api/platform/*`),
`tenant-public` (applied to tenant-runtime endpoints), and `auth` (reserved
for future authentication endpoints). Limits are deliberately generous
placeholders — per-tenant quotas are a future concern.

## What's deliberately not here yet

Business modules (Catalog, Ordering, Delivery, Reservations, ...), tenant
frontends, the Super Admin frontend, and any first-tenant implementation are
out of scope for the current foundation phase. See the module list in
`repository-structure.md` for what's planned.
