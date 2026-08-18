# 0007. Tenant Role and Permission Delegation Model

## Status

Accepted

## Context

Phase 03 gives tenant staff the ability to manage other staff and roles.
Without a delegation boundary, a tenant user with role-management access
could create a role granting any permission — including ones they don't
themselves hold — and assign it to another account (or to themselves),
escalating privilege with no further check. A separate question is how
`PlatformAdmin` (which has genuine system-wide authority) fits into that
same code path without either duplicating the role/permission logic or
accidentally becoming subject to the same restriction a tenant user faces.

## Decision

- **Subset rule.** Any operation that grants permissions — creating a role,
  changing a role's permissions, or assigning a role to a user — requires the
  permissions being granted to be a subset of the caller's own current
  effective permissions (`Identity.Authorization.DelegationGuard.EnsureSubset`).
  This applies even when a tenant user edits a role they are themselves
  assigned to.
- **PlatformAdmin is explicitly exempt**, checked via `ICurrentUser.PrincipalType`
  — never inferred from an absent tenant or a missing check. Both Tenant
  self-administration and Platform-authorized routes call the *same*
  `IRoleManagementService`/`IStaffManagementService`; the exemption lives in
  one place inside those services, not duplicated per endpoint.
- **Role names carry no authority.** Only permission keys, checked against
  the shared `IPermissionCatalog`, determine what an operation can do. A role
  named "Admin" with zero permissions grants nothing.
- **Unknown permission keys are rejected before persistence** — the catalog
  is authoritative; a typo or a reference to a not-yet-implemented business
  permission fails the request rather than silently creating a phantom grant.

## Consequences

- Privilege escalation via role/permission manipulation is structurally
  prevented, not just discouraged by convention — proven against real
  PostgreSQL in `Knight.IntegrationTests.AccessControl.PrivilegeEscalationTests`.
- `PlatformAdmin` remains the only way to bootstrap a tenant's first
  administrator (who otherwise couldn't grant themselves anything, having no
  prior effective permissions) — this is intentional, not a workaround.
- The delegation check uses the caller's permission claims already present on
  their access token, so it inherits the same short staleness window as
  ordinary authorization (see `docs/architecture/authorization.md`,
  "permission-change staleness") rather than requiring an extra database
  round trip solely for this check.
