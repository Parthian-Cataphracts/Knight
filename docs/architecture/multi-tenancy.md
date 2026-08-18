> **LEGACY DOCUMENT.** This describes the previous product (a shared
> multi-tenant food-service SaaS), not KNIGHT's target control-plane
> architecture. See [`docs/README.md`](../README.md) and
> [`docs/adr/0010`](../adr/0010-pivot-to-control-plane.md). Kept because it
> documents code that still exists in `backend/`.

# Multi-Tenancy

## Tenant model

A `Tenant` (`modules/Tenancy/Domain/Tenant.cs`) has a stable `Guid` identity, a
unique, normalized `Slug`, a `TimeZone` (validated against the IANA/`TimeZoneInfo`
database), a `DefaultCurrency` (a 3-letter ISO 4217 code), and an explicit
`TenantStatus`. Legal lifecycle transitions are enforced by the aggregate, not
by callers:

```
Pending --Activate--> Active --Suspend--> Suspended
                         |                    |
                       Archive              Activate (back to Active)
                         |                    |
                         v                    v
                      Archived <---Archive---(from Suspended)
```

`Activate` is only legal from `Pending` or `Suspended`. `Suspend` is only legal
from `Active`. `Archive` is only legal from `Active` or `Suspended` — an
archived tenant is terminal and cannot be reactivated by any ordinary
operation. No business-type or feature-specific fields live on `Tenant` — that
would break brand neutrality and cross-cut into `FeatureManagement`.

## Tenant domains

A `Tenant` owns a collection of `TenantDomain` entities (`modules/Tenancy/Domain/TenantDomain.cs`),
each mapping one normalized host to the tenant with a `TenantDomainType`
(`Primary`, `Alias`, `Admin`, `Staging`), an `IsPrimary` flag, and a
`VerificationStatus` (`Pending`/`Verified`/`Failed` — tracked for a future
automated verification workflow; resolution does not currently gate on it).

Domains are only ever created, promoted, or removed through `Tenant` methods
(`AddDomain`, `SetPrimaryDomain`, `RemoveDomain`) — never by mutating a
`TenantDomain` row directly — so the aggregate can enforce:

> **Persistence note**: a `TenantDomain` created via `Tenant.AddDomain` is
> only reachable through the already-tracked `Tenant`'s `Domains` navigation.
> Real PostgreSQL-backed testing (`docs/adr/0005-postgresql-integration-testing.md`)
> found that EF Core's change tracker did not reliably classify such a
> graph-discovered child as an insert in this model — it generated an `UPDATE`
> against a non-existent row instead of an `INSERT`. `ITenantRepository.RegisterNewDomainAsync`
> exists specifically to force `EntityState.Added` explicitly rather than
> relying on that inference; callers adding a new domain must call it before
> `SaveChangesAsync`. This was caught by, and would not have been caught
> without, the real-database integration suite.

- **Host normalization**: lowercase, no scheme, no path/query, no port,
  trailing dot stripped, validated as a well-formed multi-label hostname
  (`DomainHostFormat`). Malformed input is rejected with a clear error rather
  than silently stored inconsistently.
- **At most one primary domain per `(tenant, type)`**: promoting a new primary
  demotes the previous one in the same operation (`SetPrimaryDomain`).
- **Global host uniqueness**: enforced at the domain level (no duplicate host
  within a tenant) and, as the final guarantee against race conditions, by a
  unique database index on `tenant_domains.Host` — see `docs/database/README.md`.
  A concurrent attempt to claim an already-owned host fails with a 409
  conflict rather than silently overwriting ownership.

## Resolving the current tenant

Application and module code never touch `HttpContext`, request hosts, or
headers directly. Instead they depend on `ITenantContext`
(`Knight.Application.Abstractions.Tenancy`):

```
Guid? TenantId
bool HasTenant
bool IsPlatformContext
```

Resolution happens once per request in `TenantResolutionMiddleware`
(`Knight.Api`), which runs **after authentication** (so `context.User`
already carries validated claims) and **before authorization**. It first
checks for an authenticated Platform Admin principal (`principal_type` claim
== `platform_admin`) and, only then, explicitly elevates the request to
Platform context via `ITenantContextAccessor.SetPlatformContext()` — this is
the single, centralized, auditable place that grants cross-tenant access; see
"Platform-level data access" below. For every other request it delegates to
`ITenantResolver`. The default implementation, `DomainTenantResolver`
(`modules/Tenancy`), resolves with this precedence:

1. A trusted `tenant_id` claim on the authenticated principal (tenant-user
   access tokens always carry this claim).
2. The tenant whose primary/alias domain matches the request host.

If **both** signals are present and disagree, resolution returns a `Conflict`
outcome and the middleware rejects the request with `403 Forbidden` — a
tenant-user token is never allowed to "borrow" another tenant's host, and a
request is never resolved by silently preferring one signal over the other.

Once a candidate tenant is identified, its `TenantStatus` is checked: only
`Active` tenants resolve successfully. A `Suspended` or `Archived` tenant
produces a `Blocked` outcome (`403 Forbidden`) — the middleware never lets a
non-active tenant "silently behave as active".

If neither signal is present, resolution returns `NotResolved` and the
request proceeds with an **empty** tenant context — see "Fail closed" below.

This keeps host-based and claim-based resolution behind one seam
(`ITenantResolver`), so adding new resolution strategies later (e.g. a
development-only override, explicitly gated to non-Production environments)
doesn't touch application code.

## Fail closed

Absence of a resolved tenant must never be treated as "all tenants" or "any
default tenant". Two independent layers enforce this:

1. **Persistence**: the EF Core global query filter (below) evaluates to
   `IsPlatformContext || (HasTenant && entity.TenantId == TenantId)`. When
   neither `IsPlatformContext` nor `HasTenant` is true, every tenant-scoped
   query returns zero rows — never all rows.
2. **Endpoints**: a tenant-runtime endpoint that requires a tenant (e.g.
   `GET /api/tenant/me`) explicitly checks `ITenantContext.HasTenant` and
   throws `ForbiddenException` (-> `403`) when it is false, rather than
   assuming a tenant is present.

`Knight.IntegrationTests.Security.TenantQueryFilterIsolationTests` and
`TenantResolutionSecurityTests` prove this against a real PostgreSQL database
(see "PostgreSQL integration testing" below).

## Data isolation

Entities owned by a tenant implement `ITenantScoped` (`Knight.Domain.Common`),
which exposes a single `TenantId` property. `PlatformDbContext`
(`Knight.Infrastructure.Persistence`) applies an EF Core **global query
filter** to every `ITenantScoped` entity type automatically, via reflection
over the model at startup — individual repositories do not need to (and must
not) write `.Where(x => x.TenantId == ...)` by hand. The filter closes over the
request-scoped `ITenantContext` (not a constant), and this context is
registered via `AddDbContext` — never pooled — so every request gets its own
`PlatformDbContext` instance and there is no risk of one request's resolved
tenant leaking into another's. `TenantQueryFilterIsolationTests` specifically
exercises many alternating-tenant scopes against the one cached EF model to
regression-guard this.

`TenantDomain` deliberately does **not** implement `ITenantScoped`: host-based
tenant resolution must be able to query it *before* any tenant context exists,
and the global filter would otherwise make that lookup always return nothing.

## Platform-level data access

Platform Super Admin operations that must legitimately span tenants go through
exactly one mechanism: `ITenantContextAccessor.SetPlatformContext()`, called
only by `TenantResolutionMiddleware` and only for an authenticated
platform-admin principal. There is no second path — no repository or
application service calls `IgnoreQueryFilters()` itself; the query filter's
`IsPlatformContext` branch is the sole, centralized bypass, and it is only
ever set following explicit authentication and an explicit code path, never
inferred from an absent tenant. See `docs/architecture/authorization.md` for
the authorization side of this.

## Caching

Distributed cache keys must always be tenant-namespaced by the caller, e.g.
`tenant:{tenantId}:catalog:...`. `ICacheService` does not enforce this itself —
it is a convention modules must follow. No tenant-metadata caching has been
introduced yet; tenant/domain resolution intentionally stays a direct
repository call for now (correctness first), behind an interface
(`ITenantRepository`) that can have a caching decorator added later without
touching call sites.

## Object storage

Tenant-owned files are namespaced under `tenants/{tenantId}/...` — see
`infrastructure/storage/README.md` and `IObjectStorage`.

## PostgreSQL integration testing

Tenant isolation, resolution, and authorization behavior is proven against a
real, ephemeral PostgreSQL instance (via Testcontainers), not mocks — see
`docs/adr/0005-postgresql-integration-testing.md` and
`backend/README.md` for how to run it locally.
