# 0008. Catalog Pricing and Variant Model

## Status

Accepted

## Context

A catalog product may be sold as a single item or as a set of purchasable
variations (sizes, formats, packs). Both need a price, which raises two
questions that would become expensive to answer later: which number is the
*buyable* price when both a product-level price and variant prices exist, and
how the "one obvious choice" a storefront shows by default is kept unambiguous.
The same shape recurs for product images, where exactly one must be the primary
one used in listings.

Getting either wrong is not a cosmetic bug. Two rows claiming to be the default
variant, or a base price rendered as the price of a product that is actually
only sold by variant, mean a customer is shown a price they cannot buy at.

## Decision

- **Effective-price rule.** A product with zero variants is priced by
  `Product.BasePrice`, which is authoritative. A product with one or more
  variants is priced by `ProductVariant.Price`; `BasePrice` is then *not* the
  buyable price and is suppressed (`null`) in the public listing and detail
  responses rather than being returned alongside a `HasVariants` flag the
  caller might ignore. `CompareAtPrice` is presentational only.
- **Money is `numeric(18,2)`**, never a floating-point type, on every price
  column (`Product.BasePrice`, `ProductVariant.Price`,
  `ProductVariant.CompareAtPrice`, `Modifier.PriceDelta`). Negative values are
  rejected in the domain.
- **Single-flag invariants are enforced in three layers.** The entity owns only
  the boolean (`MarkAsDefault`/`ClearDefault`, `SetPrimary`) and never claims to
  enforce uniqueness it cannot see. The repository performs promotion as one
  atomic transactional swap — demote the incumbent and promote the successor in
  a single transaction. PostgreSQL carries a partial unique index
  (`ix_product_variants_tenant_product_default`,
  `ix_product_media_tenant_product_primary`) as the final arbiter.
- **First-wins defaulting.** The first variant created for a product and the
  first media item added to it become default/primary automatically; subsequent
  ones do not unless promotion is requested explicitly.

## Consequences

- A storefront never has to guess which price to show: `HasVariants` plus a
  suppressed `BasePrice` make the wrong rendering structurally unavailable
  rather than merely discouraged.
- Concurrent promotion of two variants (or two images) cannot leave a product
  with two defaults; the loser fails at the database. The transactional swap
  makes that outcome rare, and the index makes it impossible.
- Prices round-trip exactly rather than being approximated, verified against
  real PostgreSQL in
  `Knight.IntegrationTests.Catalog.CatalogValidationTests.PreciseDecimalPrices_RoundTripExactlyThroughPostgres`.
  The invariants are verified by reading the database back directly in
  `Knight.IntegrationTests.Catalog.CatalogInvariantTests`.
- Introducing variants for an existing single-price product changes what the
  storefront shows without any migration: the effective-price rule is derived,
  not stored.
- The cost is a small amount of duplication — the swap logic exists once per
  flagged entity — accepted in exchange for the invariant holding even when the
  application layer is bypassed.
