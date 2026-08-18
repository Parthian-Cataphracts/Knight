# 0002. Central PostgreSQL with Tenant Scoping

## Status

Accepted

## Context

Tenant data must be strongly isolated, but the platform does not yet have a
demonstrated need for the operational overhead of per-tenant databases
(provisioning, migration fan-out, cross-tenant reporting complexity).

## Decision

Use a single centralized PostgreSQL database for all tenants. Every
tenant-owned entity implements `ITenantScoped`, and `PlatformDbContext`
applies an EF Core global query filter to every such entity automatically,
scoped to the current `ITenantContext`. See
`docs/architecture/multi-tenancy.md` for the enforcement mechanism.

## Consequences

- Simpler operations: one schema, one migration pipeline, one connection pool
  to manage.
- Tenant isolation is enforced centrally rather than depending on every
  developer remembering a `WHERE TenantId = ...` clause.
- Cross-tenant queries (for Super Admin reporting) require deliberate,
  explicitly authorized use of `IgnoreQueryFilters()` — never the default.
- If a future tenant requires physical data isolation (e.g. for compliance),
  this decision will need to be revisited for that tenant specifically.
