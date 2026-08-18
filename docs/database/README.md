> **LEGACY DOCUMENT.** This describes the previous product (a shared
> multi-tenant food-service SaaS), not KNIGHT's target control-plane
> architecture. See [`docs/README.md`](../README.md) and
> [`docs/adr/0010`](../adr/0010-pivot-to-control-plane.md). Kept because it
> documents code that still exists in `backend/`.

# Database Documentation

## Schema

All tables live in the `platform` PostgreSQL schema (see
`PlatformDbContext.OnModelCreating`). The Phase 01 foundation schema:

| Table | Purpose | Notable constraints |
|---|---|---|
| `tenants` | Tenant aggregate root | unique `Slug` |
| `tenant_domains` | Hosts mapped to a tenant | unique `Host` (global); unique `(TenantId, Type)` where `IsPrimary = true` (partial index) |
| `platform_admins` | Platform Super Admin accounts | unique `NormalizedEmail` |
| `tenant_users` | Tenant-scoped user accounts | unique `(TenantId, NormalizedEmail)` |
| `roles` | Tenant-scoped named permission bundles | unique `(TenantId, NormalizedName)`; alternate key `(TenantId, Id)` |
| `role_permissions` | Individual permission grants on a role | unique `(TenantId, RoleId, PermissionKey)`; composite FK `(TenantId, RoleId) -> roles(TenantId, Id)`, cascade delete |
| `tenant_user_roles` | Role assignments to staff | unique `(TenantId, TenantUserId, RoleId)`; composite FKs to both `tenant_users` and `roles` on `(TenantId, Id)` |
| `refresh_tokens` | Revocable refresh tokens (platform + tenant subjects), grouped into rotation families | unique `TokenHash`; indexed `FamilyId`; indexed `(SubjectType, SubjectId, RevokedAt)` |
| `feature_definitions` | Platform-wide capability catalog | unique `Key` |
| `tenant_features` | Per-tenant feature enablement | unique `(TenantId, FeatureKey)` |
| `audit_log_entries` | Audit trail for platform/tenant mutations | indexed on `TenantId`, `Action`, `OccurredAt` |

Phase 04 adds the Catalog tables:

| Table | Purpose | Notable constraints |
|---|---|---|
| `categories` | Tenant-scoped product groupings | unique `(TenantId, Slug)` on the normalized slug; alternate key `(TenantId, Id)` |
| `products` | Catalog items, one per category | unique `(TenantId, Slug)`; indexed `(TenantId, CategoryId)`; composite FK `(TenantId, CategoryId) -> categories(TenantId, Id)`; `BasePrice numeric(18,2)`; alternate key `(TenantId, Id)` |
| `product_variants` | Purchasable variations of a product | unique `(TenantId, ProductId)` where `IsDefault = true` (partial index `ix_product_variants_tenant_product_default`); unique `(TenantId, NormalizedSku)` where `NormalizedSku IS NOT NULL` (`ix_product_variants_tenant_normalized_sku`); composite FK to `products(TenantId, Id)`; `Price`/`CompareAtPrice numeric(18,2)` |
| `modifier_groups` | Selection rules over a set of modifiers | indexed `TenantId`; alternate key `(TenantId, Id)` |
| `modifiers` | Individual options within a group | indexed `(TenantId, ModifierGroupId)`; composite FK to `modifier_groups(TenantId, Id)`; `PriceDelta numeric(18,2)` |
| `product_modifier_groups` | Assignment of a modifier group to a product | unique `(TenantId, ProductId, ModifierGroupId)`; composite FKs to both `products(TenantId, Id)` and `modifier_groups(TenantId, Id)` |
| `product_media` | Object-store keys attached to a product | unique `(TenantId, ProductId)` where `IsPrimary = true` (partial index `ix_product_media_tenant_product_primary`); composite FK to `products(TenantId, Id)` |

Modules own their own tables conceptually (Identity: `platform_admins`,
`tenant_users`, `roles`, `role_permissions`, `tenant_user_roles`,
`refresh_tokens`; Tenancy: `tenants`, `tenant_domains`; FeatureManagement:
`feature_definitions`, `tenant_features`), all under the single `platform`
schema — consistent with
the centralized-database decision in `docs/adr/0002-central-postgresql-with-tenant-scoping.md`.
There is deliberately no per-module database or schema-per-module split; see
that ADR for why.

`platform_admins`/`tenant_users` store both `Email` (trimmed, lowercased —
display/audit form) and `NormalizedEmail` (trimmed, uppercased — the only
column any uniqueness constraint or lookup uses), since PostgreSQL's default
string comparison is case-sensitive and would otherwise let two case-variant
emails collide or bypass uniqueness. Both also carry `FailedLoginCount` and
`LockedUntil` for server-side, persisted account lockout — see
`docs/architecture/authorization.md`.

## Migrations

Owned by `Knight.Infrastructure`, generated against `PlatformDbContext`,
using `Knight.Api` as the tools' startup project (it references
`Microsoft.EntityFrameworkCore.Design`):

```bash
dotnet ef migrations add <Name> \
  --project src/Knight.Infrastructure \
  --startup-project src/Knight.Api \
  --output-dir Persistence/Migrations
```

- `InitialCreate` (Phase 01) — creates the foundation tables listed above.
- `CompleteIdentityAuthentication` (Phase 02) — adds email normalization
  (`NormalizedEmail`, replacing the old plain-`Email` uniqueness), account
  lockout fields (`FailedLoginCount`, `LockedUntil`, `LastLoginAt`), and
  refresh-token rotation families (`FamilyId`, `ConsumedAt`,
  `ReplacedByTokenId`, `RevokedReason`, and a nullable `TenantId` binding a
  refresh token to its tenant).
- `CompleteTenantAccessControl` (Phase 03) — adds `role_permissions` and
  `tenant_user_roles` (replacing the old `roles.PermissionKeys` /
  `tenant_users.RoleIds` Postgres array columns with real join tables and
  composite foreign keys), and `roles.NormalizedName`.
- `AddCatalogModule` (Phase 04) — creates the seven catalog tables
  (`categories`, `products`, `product_variants`, `modifier_groups`,
  `modifiers`, `product_modifier_groups`, `product_media`) with their
  per-tenant slug/SKU uniqueness, the two single-flag partial unique indexes,
  and the composite tenant-consistent foreign keys.

Migrations are never applied automatically against a real database by any
tooling in this repository — applying them (`dotnet ef database update`, or
`Database.MigrateAsync()` as done by the integration test fixture against its
ephemeral container) is always an explicit, separate step.

## Tenant isolation at the schema level

`tenant_domains.Host` carries a global unique index, and the partial unique
index on `(TenantId, Type)` filtered to `IsPrimary = true` enforces "at most
one primary domain per tenant per purpose" at the database level — not just
in the `Tenant` aggregate — so a race between two concurrent requests cannot
result in duplicate domain ownership or two primary domains. See
`docs/architecture/multi-tenancy.md`.

`role_permissions` and `tenant_user_roles` go further: rather than a plain
`RoleId`/`TenantUserId` foreign key, they declare a **composite** foreign key
against `(TenantId, Id)` on the referenced table (`roles` and `tenant_users`
each expose that pair as an EF alternate key). This means PostgreSQL itself
rejects an attempted row connecting a Tenant A user to a Tenant B role (or
vice versa) at insert time — not just at query time via the EF tenant
filter — see `docs/architecture/authorization.md` ("cross-tenant foreign-key
protection") and
`Knight.IntegrationTests.AccessControl.RoleTenantIsolationTests`.

The Catalog tables mirror that same pattern throughout. Each child denormalizes
`TenantId` and declares its foreign key on `(TenantId, <ParentId>)` against the
parent's `(TenantId, Id)` alternate key: `products` → `categories`,
`product_variants` → `products`, `product_media` → `products`, `modifiers` →
`modifier_groups`, and `product_modifier_groups` → **both** `products` and
`modifier_groups`. A row connecting a Tenant A product to a Tenant B category
(or a Tenant A assignment to a Tenant B modifier group) is rejected by
PostgreSQL at insert time even when the application layer is bypassed
entirely — proven in
`Knight.IntegrationTests.Catalog.CatalogTenantIsolationTests`.
