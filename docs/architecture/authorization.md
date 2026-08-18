# Authorization

## Two identity kinds, never mixed

- **`PlatformAdmin`** (`modules/Identity/Domain/PlatformAdmin.cs`) — a Platform
  Super Admin account. Owned by the platform operator, not a tenant.
- **`TenantUser`** (`modules/Identity/Domain/TenantUser.cs`) — a user scoped to
  exactly one tenant (owner, administrator, staff, or customer, depending on
  assigned roles).

These are separate entities with separate tables, separate token issuance, and
separate claims — see `JwtAccessTokenGenerator`. A platform admin token never
carries a `tenant_id` claim; a tenant user token always does. Downstream code
tells the two apart by that claim, never by role name or convention.

**Absence of a tenant id on a request must never be treated as platform
authorization.** `ITenantContext.IsPlatformContext` is only ever set
explicitly, by code that has already authenticated and authorized the caller
as a Platform Super Admin — see `ITenantContextAccessor.SetPlatformContext()`.

## Three-layer model

```
Tenant
  |
Enabled Features        <- IFeatureAccessService (tenant-level on/off switch)
  |
Users
  |
Roles
  |
Permissions              <- IPermissionCatalog / Permission (per-action check)
```

1. **Feature** — does the tenant have access to a capability at all
   (`online-ordering`, `delivery`, `reservations`, ...)? See
   `FeatureManagement`.
2. **Role** — a tenant-scoped named bundle of permissions
   (`modules/Identity/Domain/Role.cs`), assignable to `TenantUser`s via
   `TenantUserRole`. A role's actual permission grants live in
   `RolePermission` rows (`modules/Identity/Domain/RolePermission.cs`), not on
   the role itself, so each grant can be validated against the catalog and
   protected by a real composite foreign key.
3. **Permission** — a stable, machine-readable identifier in
   `area.resource.action` form (e.g. `tenant.users.view`), declared by the
   owning module via `IPermissionProvider` and collected into the shared
   `IPermissionCatalog` at startup (`Knight.Api.Composition.ModuleRegistration`).
   The catalog rejects two providers registering the same key with
   conflicting metadata, and rejects any attempt to grant an unregistered key
   to a role (`RoleManagementService.ValidateAgainstCatalog`) — fail closed,
   never silently valid.

Feature checks and permission checks are independent: a tenant can have a
feature enabled while a specific user still lacks permission to act within
it, and vice versa is meaningless — a disabled feature blocks the action
regardless of permissions. A tenant feature being disabled does not remove
any permission from the catalog or from a role; it is purely the future
business endpoint's own job to also check `IFeatureAccessService`.

## Effective permissions and role assignment

A `TenantUser`'s effective permission set is the distinct union of every
permission key granted by every role currently assigned to them within their
own tenant (`IEffectivePermissionService`, backed by one indexed join query —
no per-role round trip). `TenantUserRole` assignment, like `RolePermission`,
carries a tenant-consistent composite foreign key, so a user can never end up
assigned a role belonging to a different tenant even through a programming
error — see "Cross-tenant foreign-key protection" below.

## Privilege delegation — critical

A `TenantUser` can never grant, via role creation, role permission changes,
or role assignment, a permission they do not currently hold themselves
(`Identity.Authorization.DelegationGuard.EnsureSubset`, applied by
`RoleManagementService` and `StaffManagementService`). Concretely:

- Creating or editing a role's permissions: the requested permission set must
  be a subset of the caller's own effective permissions — including when
  editing a role the caller is themselves assigned to.
- Assigning a role to a user: the union of permissions the role grants must
  be a subset of the caller's own effective permissions.

Role **names** carry no authority — nothing checks for `"admin"`, `"owner"`,
or any other name; only permission keys do. **`PlatformAdmin` is explicitly
exempt** from this check (verified via `ICurrentUser.PrincipalType`, never
inferred): Platform context carries its own, separately authorized global
authority and is the sanctioned way to perform an operation a tenant's own
staff cannot yet delegate to each other — e.g. bootstrapping a tenant's first
administrator before any tenant user exists.

## Cross-tenant foreign-key protection

`RolePermission` and `TenantUserRole` both carry a denormalized `TenantId`
and a **composite foreign key** — `(TenantId, RoleId) -> roles(TenantId, Id)`
and `(TenantId, TenantUserId) -> tenant_users(TenantId, Id)` — rather than a
plain `RoleId`/`TenantUserId` FK. This means PostgreSQL itself rejects a row
that tries to connect a Tenant A user (or role) to a Tenant B role, even if
application code has a bug; it is not just the EF tenant query filter (which
would reject *reading* such a row, not *inserting* one). See
`docs/database/README.md` and
`Knight.IntegrationTests.AccessControl.RoleTenantIsolationTests.CrossTenantRoleAssignment_IsRejectedByDatabase`.

## Server-side enforcement only

Every feature and permission check must be enforced in `Knight.Application`
or deeper. Hiding a menu item in a future frontend is a UX nicety, never a
security control — see `ForbiddenException` for the standard way application
code rejects a disallowed action, translated to HTTP 403 by
`ExceptionHandlingMiddleware`.

## Authorization policies

Two claim-based ASP.NET Core authorization policies, registered in
`Knight.Api`'s composition root, are the sole gate on Platform vs. tenant
endpoints:

- `PlatformAdminOnly` — requires the `principal_type` claim to equal
  `platform_admin`. Applied to `/api/platform/tenants` and
  `/api/platform/auth/{logout-all,me,change-password}`.
- `TenantUserOnly` — requires `principal_type` to equal `tenant_user`.
  Applied to `/api/tenant/me` and the equivalent `/api/tenant/auth/*` endpoints.

A third mechanism, `PermissionRequirement`/`PermissionAuthorizationHandler`
(`Knight.Api.Authorization`), lets an endpoint additionally require a
specific permission claim — `.RequireAuthorization(p => p.RequirePermission("catalog.products.view"))` —
checked against `ICurrentUser.HasPermission`, with no controller-specific
database query or role switch statement. No business module uses this yet;
it exists so one can adopt it directly.

All of these are claim checks on an already-validated JWT, not role names or
conventions, and reject anonymous callers outright (`401`) as well as the
wrong principal type or missing permission (`403`). No endpoint contains ad
hoc `if (isPlatformAdmin)` branching — the framework's authorization pipeline
is authoritative.

## JWT validation

`Knight.Api` validates, on every request: issuer, audience, signature
(HMAC-SHA256 over an environment-provided signing key — never committed, and
`JwtProductionSigningKeyValidator` refuses to start in Production with the
known development placeholder value), and lifetime (with a small clock-skew
allowance). `options.MapInboundClaims = false` is set explicitly — without it,
ASP.NET Core's default JWT handler silently renames well-known claims (e.g.
`sub` to the long legacy `ClaimTypes.NameIdentifier` URI), which would break
every claim lookup that expects the claim types `JwtAccessTokenGenerator`
actually issued.

The `principal_type` claim is what distinguishes a platform-admin token from
a tenant-user token; a token missing a `tenant_id` claim is never inferred to
be platform-level — only an explicit `principal_type=platform_admin` claim
grants that, checked by the `PlatformAdminOnly` policy and, independently, by
`TenantResolutionMiddleware` before it will call `SetPlatformContext()`.
Access tokens also carry a unique `jti` claim (no server-side blacklist
consults it today — see "Access token revocation" below).

When a tenant-user token's `tenant_id` claim and a host-resolved tenant
disagree, `DomainTenantResolver` returns a `Conflict` outcome and the request
is rejected with `403` — see `docs/architecture/multi-tenancy.md`. This
prevents a valid token for one tenant from being replayed against another
tenant's domain (a confused-deputy scenario). The same context check (subject
type + bound tenant) gates refresh-token rotation — see "Refresh sessions" below.

## Authentication routes

```
POST /api/platform/auth/login
POST /api/platform/auth/refresh
POST /api/platform/auth/logout
POST /api/platform/auth/logout-all
GET  /api/platform/auth/me
POST /api/platform/auth/change-password

POST /api/tenant/auth/login
POST /api/tenant/auth/refresh
POST /api/tenant/auth/logout
POST /api/tenant/auth/logout-all
GET  /api/tenant/auth/me
POST /api/tenant/auth/change-password
```

There is deliberately **no public registration endpoint** for either
principal type, and never will be for `PlatformAdmin`. `PlatformAdmin`
accounts are provisioned only via `tools/Knight.Bootstrap` (a manual,
offline console tool — see "Platform admin bootstrap" below); `TenantUser`
provisioning will be exposed through a future controlled administrative API,
not public self-registration. A future end-customer identity is an
intentionally separate concept from `TenantUser` (which means
administrative/staff identity) and will get its own module.

Tenant login/refresh/`/me` never accept a tenant selector from the request
body — the tenant is always the one `ITenantContext` already resolved from
the request host (see `docs/architecture/multi-tenancy.md`); a request that
doesn't resolve to a tenant is rejected before any credential check runs.

## Login enumeration resistance

Every login failure — unknown email, wrong password, locked account,
disabled account — returns the exact same `401` response externally
(`AuthResponses.GenericUnauthorized`). `CredentialVerifier`
(`modules/Identity/Authentication`) still performs a real password-hash
verification even when the account doesn't exist (against a cached dummy
hash computed once), so an unknown email does not complete measurably faster
than a real one. The specific reason is only ever visible internally, in
structured logs/audit events (e.g. `AccountLocked`).

## Account lockout

`PlatformAdmin` and `TenantUser` (both implementing `Identity.Domain.ILockableAccount`)
track `FailedLoginCount` and `LockedUntil`. Configurable via the `Lockout`
configuration section (`Identity.Options.LockoutOptions`):

- `FailedAttemptThreshold` (default 5)
- `LockoutDuration` (default 15 minutes)

A failed login increments the counter and, at the threshold, sets
`LockedUntil`; a successful login resets both. Lockout is evaluated
server-side, persisted, and re-checked on every login — never inferred from
client state. For `TenantUser`, tenant status remains authoritative
independently: an `Active` user inside a `Suspended`/`Archived` tenant still
cannot use tenant runtime functionality, enforced by
`TenantResolutionMiddleware` before any endpoint runs.

## Password policy and hashing

`Identity.Options.PasswordPolicyOptions` (config section `PasswordPolicy`)
enforces a minimum (default 10) and maximum (default 128) length; passwords
are never silently truncated. Hashing is PBKDF2-HMAC-SHA256
(`Pbkdf2PasswordHasher`, unchanged from Phase 00/01). `IPasswordHasher.NeedsRehash`
lets a successful login transparently rehash a password whose stored work
factor has fallen behind the hasher's current target — `PlatformAuthenticationService`/
`TenantAuthenticationService` do this on every successful login.

Changing a password (`POST .../auth/change-password`) requires the current
password, revokes every existing refresh session for that principal (see
below), clears the caller's refresh cookie, and records a `PasswordChanged`
audit event. The already-issued access token is not individually invalidated —
it simply expires on its own short lifetime; see "Access token revocation"
below for why.

Forgot-password/reset-password delivery is explicitly **not** implemented —
there is no notification/email module yet. It is a future Identity +
Notifications capability.

## Refresh sessions

Refresh tokens (`Identity.Domain.RefreshToken`) are opaque, cryptographically
random 256-bit values (`RefreshTokenGenerator`, base64url-encoded for
transport) — never JWTs. Only a SHA-256 hash is ever persisted; the raw value
exists solely in the response cookie at issuance time.

Every login starts a **family** (`FamilyId`) with one **absolute** expiration
(`ExpiresAt`, configurable per principal type via `RefreshToken:PlatformFamilyLifetime` /
`RefreshToken:TenantFamilyLifetime`, defaulting to 12 hours / 30 days).
Rotation (`RefreshTokenService.RotateAsync`) issues the family's next token
carrying the *same* `FamilyId` and the *same* `ExpiresAt` — rotation never
extends a family's lifetime, it only replaces which token is currently valid.

Rotation is atomic at the database level
(`IRefreshTokenRepository.TryConsumeAndIssueAsync`): a single conditional
`UPDATE ... WHERE ConsumedAt IS NULL AND RevokedAt IS NULL`, inside an
explicit transaction with the new token's insert, so two concurrent requests
racing to consume the same token can never both succeed — PostgreSQL's row
lock guarantees only one `UPDATE` affects a row. **Reuse detection**: if a
token that is already consumed (or revoked) is presented again, the entire
family is revoked, a `RefreshTokenReuseDetected` + `RefreshFamilyRevoked`
audit pair is recorded, and the request is denied. This applies uniformly
whether the second presentation is genuine attacker replay or a benign lost
race — the conservative, deliberately chosen outcome in both cases is that
reauthentication becomes required (see
`docs/adr/0006-refresh-token-rotation-and-session-strategy.md`).

On every refresh, the current `PlatformAdmin`/`TenantUser` is re-loaded and
re-checked (`CanAuthenticate`) — a stored refresh token stops working the
moment the account is disabled or locked, without waiting for it to expire.
Tenant status is re-checked the same way structurally, via
`TenantResolutionMiddleware` running ahead of the tenant refresh endpoint on
every request.

`Logout` revokes the family owning the presented cookie (safe/idempotent if
absent) and clears the cookie. `Logout-all` revokes every active family for
the calling principal only — never another user's, never other tenants'.

## Refresh cookie transport

The refresh token is delivered only via an `HttpOnly` cookie, never in a JSON
response body. Platform and Tenant use separate cookie names and `Path`
scopes (`/api/platform/auth`, `/api/tenant/auth`) so they can never collide
or be sent to the other's endpoints. Outside Development the cookie name
carries the browser-enforced `__Host-` prefix (requiring `Secure=true`, no
`Domain` attribute, `Path=/` from the browser's perspective — ours is
narrower via routing) and `SameSite=Strict`; Development uses a plain name,
`Secure=false`, `SameSite=Lax` since local HTTPS is often unavailable — see
`Knight.Api.Composition.AuthCookies`. Production never silently downgrades
this.

**Future frontend deployment assumption**: tenant storefront/admin
applications will eventually live on independently deployed custom domains.
For refresh cookies to remain first-party (no third-party cookie
dependency), the intended model is same-origin access to the platform API —
either a reverse proxy (`tenant-domain.example/api/* -> central API`) or an
equivalent BFF. That proxy is not implemented in this phase; cookie security
is not weakened to compensate for its absence.

CORS stays restrictive (explicit allowed origins only, no wildcard with
credentials) as the CSRF mitigation appropriate for this design — no
additional CSRF token framework is layered on top of bearer-protected APIs.

## Access token revocation

Access tokens are short-lived by design (5 minutes for `PlatformAdmin`, 10
for `TenantUser` — `Jwt:PlatformAccessTokenLifetimeMinutes` /
`Jwt:TenantAccessTokenLifetimeMinutes`) specifically so logout/revocation
does not require a database lookup on every authenticated request. Logout,
logout-all, and password change all prevent *future* refreshes; an
already-issued access token remains valid until it naturally expires. There
is no JWT blacklist — the short lifetime is the bound on staleness.

**Permission-change staleness** follows the same bound: a `permission` claim
already baked into an issued Tenant access token can remain in effect for up
to that token's remaining lifetime (≤ 10 minutes) after an administrator
changes the underlying role. This is a deliberate trade-off, not an
oversight — adding a database lookup to every permission-gated request
purely to make role changes take effect instantly was explicitly rejected as
disproportionate for this phase (`.RequirePermission(...)` stays a pure claim
check). **Refresh always re-resolves current roles and permissions from the
database** (`TenantAuthenticationService.RefreshAsync` calls
`IEffectivePermissionService` fresh every time — it never copies claims
forward from the token being rotated), so the staleness window cannot exceed
one access-token lifetime regardless of how long a session has been open.
**Account disable is different and not subject to this staleness window**:
disabling a `TenantUser` immediately revokes every refresh session
(`StaffManagementService.DisableAsync`), so no *future* refresh or login can
succeed — only an already-issued, not-yet-expired access token remains
usable, same as any other logout.

## Rate limiting

Login and refresh endpoints have dedicated, per-client-IP-partitioned rate
limit policies (`auth-platform-login`, `auth-tenant-login`, `auth-refresh`),
configurable via the `RateLimiting` section
(`Knight.Api.Composition.RateLimitOptions`) — never hardcoded. Rate
limiting is defense-in-depth alongside account lockout, not a replacement for
it: lockout stops a specific account from being brute-forced regardless of
which IPs the attempts come from; rate limiting bounds request volume from a
given source regardless of which accounts are targeted.

## Platform admin bootstrap

There is no public registration endpoint, and there must never be one for
`PlatformAdmin`. The first (and any subsequent) platform admin is provisioned
by running `tools/Knight.Bootstrap` manually and offline:

```bash
PLATFORM_DB_CONNECTION_STRING="..." dotnet run --project tools/Knight.Bootstrap -- --email admin@example.com
```

It prompts for the password interactively (masked, confirmed) — the password
is never accepted as a command-line argument, which would leak into shell
history. It is idempotent against an already-existing normalized email (it
reports the existing account and makes no changes) and is not invoked by API
startup.

## Platform admin MFA (deferred)

Multi-factor authentication for `PlatformAdmin` is a **mandatory
pre-production requirement**, not yet implemented. No TOTP/HOTP/WebAuthn
cryptography is hand-rolled here or planned to be — a future dedicated
security phase will add a vetted, standards-compliant mechanism. Nothing in
the current token/session architecture (principal separation, short-lived
access tokens, revocable refresh families) needs to change to accommodate
that later. Do not treat Platform authentication as production-ready without it.

## Impersonation

No tenant-impersonation mechanism exists yet. When one is built, it must be
an explicit, audited operation with its own dedicated code path — never
implemented as a Platform Admin arbitrarily setting `ITenantContextAccessor`
to any `TenantId` of their choosing. The current `SetPlatformContext()` /
`SetTenant()` split already keeps "acting as the platform" and "acting as a
specific tenant" as distinct states, which a future impersonation feature can
build on without redesigning tenant resolution.

## Auditing

`IAuditLogger` (`Knight.Application.Abstractions.Auditing`) records
`ActorUserId`, `ActorType`, `TenantId` (when applicable), `Action`,
`EntityType`/`EntityId`, metadata, and a timestamp for significant actions.
The default implementation, `EfAuditLogger` (`Knight.Infrastructure.Auditing`),
persists each entry to the `audit_log_entries` table — deliberately not
tenant-scoped (not `ITenantScoped`), since Platform Super Admin actions span
tenants and must remain auditable regardless of the current tenant query
filter. `TenantManagementService` and `TenantFeatureManagementService` call it
for every mutation (`TenantCreated`, `TenantActivated`, `TenantSuspended`,
`TenantArchived`, `TenantDomainAdded`, `TenantDomainRemoved`,
`TenantPrimaryDomainChanged`, `TenantFeatureEnabled`, `TenantFeatureDisabled`).
`PlatformAuthenticationService`/`TenantAuthenticationService` and
`RefreshTokenService` additionally record `PlatformLoginSucceeded`,
`TenantLoginSucceeded`, `AccountLocked`, `PasswordChanged`, `LogoutAll`,
`RefreshTokenReuseDetected`, and `RefreshFamilyRevoked`. Failed-login attempts
are deliberately **not** individually audited (that would let an attacker
flood the audit table) — only the resulting lockout is. Implementations must
never receive secrets (passwords, tokens, JWTs) as metadata, and none of the
authentication code paths above ever pass one.

## API error semantics

Every failure leaves the API as an RFC 7807 Problem Details body built by
`ExceptionHandlingMiddleware` (`Knight.Api.Middleware`), carrying a `status`,
`title`, `type`, `instance`, an `errorCode` from `ApiErrorCodes`, and the request
`correlationId`. Stack traces, inner-exception text, connection strings, SQL, and
PostgreSQL constraint names are logged but never serialized into the response —
`UniqueConstraintViolationException` in particular wraps the raw `DbUpdateException`
as an inner exception and is surfaced only as the generic title "The request
conflicts with existing data."

| Condition | Status |
| --- | --- |
| `ValidationException`, `DomainException` with `Category = Validation` | 400 |
| Missing or invalid bearer token (standard ASP.NET Core authentication) | 401 |
| `ForbiddenException` | 403 |
| `NotFoundException` | 404 |
| `ConflictException`, `UniqueConstraintViolationException`, `DomainException` with `Category = Conflict` | 409 |
| Anything else | 500 |

401 has no custom exception behind it: it is produced by the authentication
middleware rejecting the request before a handler runs.

### The two domain error categories

`Knight.Domain.Exceptions.DomainException` carries a
`DomainErrorCategory` (`Validation` or `Conflict`) fixed at construction and
selected through the `DomainException.Validation(...)` / `DomainException.Conflict(...)`
factories. The API switches on that property, so a status code is never inferred
from a message string, and a single exception type keeps the hierarchy flat — no
per-field or per-entity subclasses.

The category is a **semantic signal, not an HTTP code**. `Knight.Domain`
references nothing transport-related (enforced by an architecture test barring
`Microsoft.AspNetCore.*` from the domain assembly); the translation from category
to status happens only in the API layer, and a different transport could map the
same categories differently.

**`Validation`** — the input is malformed, out of range, or self-contradictory,
and would be invalid against any stored data. Negative `Product.BasePrice`,
`ProductVariant.Price`, `CompareAtPrice`, or `Modifier.PriceDelta`; a
`ModifierGroup` with `MinSelections < 0`, `MaxSelections < MinSelections`, or
required with a zero minimum; a slug that normalizes to nothing; a
`ProductMedia.StorageKey` shaped like a filesystem path; empty required
identifiers on `Role`, `RolePermission`, `TenantUserRole`, `TenantFeature`, and
`FeatureDefinition`; and `Tenant` name, slug, time zone, and currency format
rules.

**`Conflict`** — the input is well formed but collides with existing state, so
the same request could succeed against a different database. A duplicate tenant,
category, or product slug; a duplicate variant SKU or staff email; a host already
mapped to a tenant; deleting a category that still holds products, a modifier
group still assigned to products, or a role still assigned to users; and
`Tenant` lifecycle transitions illegal for the aggregate's current status.
