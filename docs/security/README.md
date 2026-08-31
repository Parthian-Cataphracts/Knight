> **LEGACY DOCUMENT.** This describes the previous product (a shared
> multi-tenant food-service SaaS), not KNIGHT's target control-plane
> architecture. See [`docs/README.md`](../README.md) and
> [`docs/adr/0010`](../adr/0010-pivot-to-control-plane.md). Kept because it
> documents code that still exists in `backend/`.

# Security Notes

## Secrets

No connection strings, signing keys, or credentials are committed to this
repository. Local development values live in `appsettings.Development.json`
as clearly non-production placeholders; real environments must supply their
own via environment variables or a secret store.

## Passwords

Passwords are hashed with PBKDF2-HMAC-SHA256 (210,000 iterations, random
16-byte salt per password) — see `Knight.Infrastructure/Security/Pbkdf2PasswordHasher.cs`.
Plaintext passwords are never logged or persisted. A configurable policy
(`PasswordPolicy` section) enforces minimum/maximum length; a login that
succeeds against a hash with an outdated work factor transparently rehashes
it. See `docs/architecture/authorization.md`.

## Tokens

Access tokens are short-lived signed JWTs (5 minutes Platform, 10 minutes
Tenant); refresh tokens are opaque 256-bit random values, persisted only as a
SHA-256 hash, grouped into rotation families with atomic single-use
consumption and reuse detection — see `modules/Identity/Domain/RefreshToken.cs`
and `docs/architecture/authorization.md` for the full rotation/reuse model.
The refresh token is delivered only via an `HttpOnly` cookie, never in a JSON
response body.

## Account lockout and login enumeration resistance

Both `PlatformAdmin` and `TenantUser` lock out after a configurable number of
failed attempts (`Lockout` section) and stay locked for a configurable
duration. Every login failure — unknown email, wrong password, locked,
disabled — returns the identical external response; a dummy password
verification runs even for unknown emails so response timing does not reveal
account existence. See `docs/architecture/authorization.md`.

## No public registration — for admins and staff

> **Updated by [`adr/0035`](../adr/0035-pivot-to-self-service-saas-registration.md).**
> The **customer/merchant** principal now registers publicly via
> `POST /api/v1/auth/register` (rate-limited, email-verified, no
> account-existence oracle). What follows still holds for administrators and
> staff, which remain non-registrable.

There is no `/api/platform/auth/register` and there never will be one —
`PlatformAdmin` accounts are provisioned only via `tools/Knight.Bootstrap`,
run manually and offline. There is likewise no self-registration for
`TenantUser` **staff** accounts — they are provisioned only by an
already-authenticated `TenantUser` (with `tenant.users.create`) or
`PlatformAdmin`, via `POST /api/tenant/staff` or
`POST /api/platform/tenants/{tenantId}/staff` (see "Tenant staff provisioning
and lifecycle" below). The end-customer
identity is a separate concept from `TenantUser` and will not reuse it.

## Tenant role and permission delegation

Creating or editing a role's permissions, or assigning a role to a staff
member, is limited to a subset of the caller's own current effective
permissions — a tenant user can never grant authority they don't themselves
hold, even to a role they own. `PlatformAdmin` is explicitly exempt from this
check (it is how a tenant's first role/administrator is bootstrapped). See
`docs/adr/0007-tenant-role-permission-delegation-model.md` and
`Identity.Authorization.DelegationGuard`. Role names carry no authority by
themselves — only the permission keys attached to a role, validated against
the shared permission catalog, are ever checked; unknown permission keys are
rejected before anything is persisted.

## Tenant staff provisioning and lifecycle

An admin-supplied initial password is hashed immediately on staff creation
and is never logged, echoed back in a response, or persisted in plaintext.
Staff creation and its initial role assignment happen in one atomic
transaction — a staff account is never left with zero roles due to a partial
failure. Disabling a staff account immediately revokes all of that user's
active refresh-token sessions and blocks further logins and refreshes;
re-enabling a previously disabled account does **not** resurrect its old
sessions — the user must log in again. Locked accounts (from failed-login
lockout) can be administratively unlocked without a password reset. There is
no hard delete of a `TenantUser` in this phase — disable is the only
supported removal path, preserving audit history and existing foreign-key
references. Session revocation triggered by an administrator is scoped to
the target user only, never to other users in the same tenant.

## Catalog feature and permission independence

Every catalog route requires two conditions that are checked independently: the
tenant must have the `catalog` feature enabled, and the caller must hold the
relevant `catalog.*` permission. Holding the permission while the feature is off
is denied, and having the feature on without the permission is denied; both
surface as HTTP 403. The feature check is declared on the route group with
`.RequireFeature(CatalogFeature.Key)` — the `FeatureGateEndpointFilter` in
`src/Knight.Api/Authorization/FeatureGate.cs`, the feature counterpart of
`.RequirePermission(...)`. Declaring it on the group is what makes it
unloseable: a newly added route inherits the gate instead of depending on the
author writing a first statement. The filter is module-agnostic (it takes a
feature key string), runs inside the endpoint pipeline so
`ExceptionHandlingMiddleware` still shapes its `ForbiddenException` into the
same Problem Details response, and fails closed on every path — no tenant
context, an unparseable route tenant, an unregistered feature key, or a tenant
with the feature off all deny identically. Platform routes use
`.RequireFeatureForRouteTenant(...)`, which reads the target tenant from the
`{tenantId}` route value rather than from `ITenantContext`.
`PlatformAdmin` needs no `catalog.*` permission on the
platform-side mirror routes, but the target tenant's feature flag still
applies. See `docs/architecture/catalog.md` and
`Knight.IntegrationTests.Catalog.CatalogFeaturePermissionTests`.

## Catalog tenant-enumeration resistance

A catalog identifier belonging to another tenant produces the same plain 404 as
an identifier that never existed. No response body discloses that a resource
exists elsewhere — there is no "belongs to another tenant" wording, no other
tenant's identifier, and no host — so the API cannot be used to probe for the
existence of another tenant's categories or products. Underneath, composite
foreign keys make a cross-tenant row impossible at the database level as well,
so isolation does not depend on the query filter alone.

## Platform admin MFA — deferred, mandatory before production

Multi-factor authentication for `PlatformAdmin` is **not implemented** and is
a mandatory requirement before any production deployment, given the
system-wide authority a platform admin account holds. See
`docs/architecture/authorization.md` for what is and isn't in place today.

## Tenant isolation

See `docs/architecture/multi-tenancy.md` — global EF Core query filter,
fail-closed behavior, and the single centralized platform-context bypass.

## Platform vs. tenant authorization

See `docs/architecture/authorization.md` — the `PlatformAdminOnly` /
`TenantUserOnly` policies, JWT claim validation, and confused-deputy
protection (host/token tenant mismatch rejection).

## Audit logging

Every Platform tenant-management mutation (create, activate, suspend,
archive, domain add/remove/primary-change, feature enable/disable) is
recorded in `audit_log_entries` with actor, action, tenant, and timestamp —
see `docs/architecture/authorization.md`. Audit metadata never includes
passwords, tokens, or other secrets.

## Reporting a concern

This is an internal foundation-phase repository; route security concerns to
the platform engineering team directly.
