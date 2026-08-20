# 0023 — A ported store is single-tenant, and the port drops `TenantId`

- Status: **Accepted**
- Date: 2026-08-20

## Context

Phase 8 ports `Catalog`, `Ordering`, `Checkout`, `Payment`, `Promotions`,
`Fulfillment` and `Delivery` from the frozen .NET modules into Django. Those
modules were written for the product KNIGHT replaced: **one .NET application
serving many tenants out of one database**, with `TenantId` on every aggregate
and a tenant filter on every query.

KNIGHT's model is the opposite one ([`adr/0010`](0010-pivot-to-control-plane.md),
`docs/README.md` rule 3): a store is an independent Django application with its
own database, which KNIGHT manages and never connects to. There is exactly one
store per deployment.

So the port has to decide what happens to `TenantId`. The tempting answer is to
keep it — porting is safer when it is mechanical, and a column that is always
the same value costs nothing to carry.

## Options considered

1. **Keep `TenantId`, always set to the store's own id.** Mechanical, and every
   ported query keeps its filter. But it preserves a concept the new
   architecture does not have. Every model gains a field nobody sets meaningfully,
   every query keeps a filter that can only ever match, and every new developer
   has to be told that multi-tenancy is vestigial — which is exactly the kind of
   thing that stops being told after a year. Worse, a filter that is *always*
   satisfied is a filter nobody notices removing, and the day somebody makes a
   store serve two brands, a half-remembered `TenantId` is a security model that
   looks present and is not.
2. **Keep it behind a compatibility shim** so the ported code reads as it did.
   All the cost of option 1, plus a layer.
3. **Drop it.** The isolation boundary moves from a column to a database.

## Decision

**Option 3.** A ported store is single-tenant, and `TenantId` does not survive
the port.

Isolation between customers is enforced by the fact that their stores are
different applications with different databases and different credentials — a
boundary that cannot be bypassed by forgetting a `where` clause, which is
precisely the failure the original design had to defend against constantly.

Concretely:

- Every ported model loses `TenantId`, and every uniqueness constraint that was
  `(TenantId, Slug)` becomes `Slug`.
- `TenantOrderCounter` becomes a single row: order numbers are sequential within
  the store, because there is only one.
- `TenantFulfillmentSettings` and `TenantDeliverySettings` become singletons —
  settings *of the store*, not settings *per tenant within* the store.
- Anything named `Tenant*` is renamed to say what it now is.

`Customer` is renamed to **`Shopper`**. In KNIGHT's vocabulary a *customer* is
the business that buys KNIGHT; the person buying a sandwich from one of their
stores is a different party entirely, and having one word mean both across two
codebases is how somebody eventually writes the wrong query.

## Consequences

**Positive** — the ported code is markedly simpler than its source: no tenant
filters, no tenant-scoped repositories, no risk of a query missing one. The
isolation guarantee gets stronger, not weaker, because it stops depending on
developer discipline. Models read as what they are.

**Negative** — the port stops being mechanical, so it takes longer and needs
review rather than translation. And a future in which one store deployment
serves several brands would need real work rather than an existing column; that
is the correct trade, because such a store would need a genuine multi-tenancy
design and not a resurrected field.

**Also** — parity tests cannot be a line-by-line comparison against the .NET
suites, since the shapes differ deliberately. They compare *behaviour*: the same
order, priced the same way, transitions the same way and is refused for the same
reasons.
