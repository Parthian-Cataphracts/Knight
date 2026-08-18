> **LEGACY DOCUMENT.** This describes the previous product (a shared
> multi-tenant food-service SaaS), not KNIGHT's target control-plane
> architecture. See [`docs/README.md`](../README.md) and
> [`docs/adr/0010`](../adr/0010-pivot-to-control-plane.md). Kept because it
> documents code that still exists in `backend/`.

# Catalog

The Catalog module owns the tenant product catalog: categories, products,
variants, modifier groups and modifiers, product media, and the anonymous
storefront read surface.

## Module boundary

Domain entities, repository interfaces and application services live in
`backend/modules/Catalog` — exactly like `modules/Identity`,
`modules/Tenancy` and `modules/FeatureManagement`. The EF Core entity
configurations, migrations and repository implementations live in
`Knight.Infrastructure/Persistence`, so the module itself never references
EF Core, ASP.NET Core or `PlatformDbContext`. This is enforced, not just
conventional: `Knight.ArchitectureTests.LayeringTests`
(`Modules_ShouldNotDependOn_Infrastructure`, `Modules_ShouldNotDependOn_Api`)
includes the Catalog domain assembly.

Contracts (`Knight.Contracts/Catalog`) and endpoints
(`Knight.Api/Endpoints/*Catalog*`) sit above the module. Endpoints hold no
business logic; each request record maps 1:1 onto an application-service input
record.

## Effective price

- A product with **no variants**: `Product.BasePrice` is the authoritative,
  buyable price.
- A product with **one or more variants**: `BasePrice` is *not* the buyable
  price — each `ProductVariant.Price` is. The public listing and detail
  responses therefore suppress `BasePrice` (send `null`) whenever
  `HasVariants` is true, rather than returning a number a storefront could
  mistakenly render as the price.

`CompareAtPrice` on a variant is presentational only (a struck-through
reference price); it participates in no calculation. All money columns are
`numeric(18,2)` — never a floating-point type.

See `docs/adr/0008-catalog-pricing-and-variant-model.md`.

## Single-flag invariants

Two invariants must hold at all times: **exactly one default variant per
product** and **exactly one primary media item per product**. Each is enforced
at three levels:

1. The domain entity owns only the flag (`MarkAsDefault`/`ClearDefault`,
   `SetPrimary`) — it cannot see its siblings, so it never claims to enforce
   uniqueness.
2. The repository performs the promotion as one atomic transactional swap:
   demote the current holder and promote the new one in a single transaction.
3. PostgreSQL carries a partial unique index as the last line of defense —
   `ix_product_variants_tenant_product_default` filtered to `IsDefault = true`
   and `ix_product_media_tenant_product_primary` filtered to `IsPrimary = true`.
   A concurrent double-promotion loses at the database, not silently.

The first variant created for a product and the first media item added to a
product become the default/primary automatically; later ones do not, unless
promotion is requested explicitly.

## Modifier model

- `ModifierGroup` owns the selection rules — `IsRequired`, `MinSelections`,
  `MaxSelections` — validated together in the domain: a negative minimum, a
  maximum below the minimum, and a required group permitting zero selections
  are all rejected at construction and on every update.
- `Modifier` belongs to exactly one group and carries a non-negative
  `PriceDelta`.
- `ProductModifierGroup` is the reusable assignment join: one group can be
  attached to many products. Assignment is replace-all — the submitted set
  becomes the product's complete list, and an empty set clears it.

## Availability versus visibility

These are deliberately separate concepts with separate permissions:

- **Visibility** (`IsVisible`) is publishing — is this in the storefront at
  all? Editing it requires `catalog.products.update` /
  `catalog.categories.update`, because unpublishing is content editing.
- **Availability** (`IsAvailable`) is the day-to-day "can it be ordered right
  now" toggle. Only the dedicated `/availability` endpoints use
  `catalog.availability.manage`, so a front-of-house role can be given
  availability control without ever gaining the ability to edit or unpublish
  catalog content.

The public visibility matrix follows from that:

| Product state | Returned publicly? |
|---|---|
| Active, visible, available | yes |
| Active, visible, unavailable | yes — with `IsAvailable = false`, so the storefront can render it as sold out |
| Hidden (`IsVisible = false`) | no |
| Draft | no |
| Archived | no |

A hidden, draft or archived product is absent from the public list and 404s on
a direct slug lookup, regardless of its other flags.

## Feature and permission composition

Reaching the tenant catalog requires **both** conditions, independently:

- the tenant has the `catalog` feature enabled (`IFeatureAccessService`), and
- the caller holds the relevant `catalog.*` permission.

Neither alone is sufficient. The feature check is declared once per route group
as `.RequireFeature(CatalogFeature.Key)`, backed by `FeatureGateEndpointFilter`
(`src/Knight.Api/Authorization/FeatureGate.cs`) — the feature analogue of
`.RequirePermission(...)`. Group-level declaration is why the gate cannot be
lost when a route is added, moved or re-grouped: the route inherits it. The
filter takes a plain feature key and knows nothing about Catalog, and it fails
closed with `ForbiddenException` → HTTP 403 whenever the tenant cannot be
resolved or the feature is not enabled — including for a feature key that was
never registered at all.

The check applies uniformly: tenant admin routes, platform admin routes (via
`.RequireFeatureForRouteTenant(...)`, which resolves the target tenant from the
`{tenantId}` route value) and the anonymous public routes, which carry the gate
without any authorization policy. A
platform admin needs no `catalog.*` permission — the `PlatformAdminOnly` policy
is its authority — but is still subject to the target tenant's feature flag.

Catalog declares its 12 permissions through `IPermissionProvider`, so
registering the module is all it takes for them to appear in the shared
`IPermissionCatalog`; no Identity-side change is required.

## Delete semantics

| Entity | DELETE means | Guard |
|---|---|---|
| Product | archive (`Status = Archived`), row retained | — |
| Variant | deactivate (`IsAvailable = false`), row retained | — |
| Category | physical delete | 409 while any product still references it |
| ModifierGroup | physical delete | 409 while any product assignment still references it |
| ProductMedia | physical delete | — |

Products and variants are never physically removed so that future order history
stays resolvable. Categories and modifier groups have no such retention
concern, but both are conflict-checked so a delete can never silently orphan or
cascade away live catalog data; media rows carry nothing worth retaining.

## Tenant relational integrity

Every catalog relation uses the composite foreign-key pattern established in
Phase 03 (see `docs/architecture/multi-tenancy.md`): the child denormalizes
`TenantId` and declares its foreign key on `(TenantId, <ParentId>)` against the
parent's `(TenantId, Id)` alternate key. PostgreSQL therefore rejects a Tenant A
product pointing at a Tenant B category — at insert time, not merely at query
time via the EF tenant filter. The relations covered are Product→Category,
Variant→Product, Media→Product, Modifier→ModifierGroup, and
ProductModifierGroup→Product **and** →ModifierGroup.

Cross-tenant reads through the API return a plain scoped 404 that is
indistinguishable from "never existed" — no response ever discloses that a
resource belongs to another tenant.
