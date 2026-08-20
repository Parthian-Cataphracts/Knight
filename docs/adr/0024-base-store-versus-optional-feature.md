# 0024 — What belongs to the base store, and what is an optional Feature

- Status: **Accepted**
- Date: 2026-08-20

## Context

Phase 8 asks, per capability, whether it belongs to the base store image or is
sold as an optional Feature ([`adr/0014`](0014-features-as-deployable-packages.md)).
The question has to be answered once, with a rule, rather than case by case —
because the answer determines what a customer on the cheapest plan actually
receives, and moving a capability across that line later means migrating data on
every store that has it.

## The rule

**A capability is base if a store cannot function commercially without it.**
Everything else is a Feature.

"Function commercially" means: a shopper can find something, buy it, and the
store can record having been paid and having handed it over. Anything that makes
that better, cheaper or more measurable is a Feature.

Two secondary tests, applied when the first is ambiguous:

- **Would removing it leave a broken store or a plainer one?** Removing coupons
  leaves a store that sells at list price. Removing the catalogue leaves nothing.
- **Does it carry data the store must keep even when unsold?** A Feature's
  data survives its entitlement being revoked ([`adr/0016`](0016-feature-migration-and-removal-policy.md)),
  which is workable for a promotions history and absurd for the order table.

## Decision

**Base store** — shipped in the image, present on every store, never entitled:

| Capability | Ported as | Why base |
|---|---|---|
| Catalogue | `apps.catalog` | Nothing to sell without it |
| Shoppers | `apps.shoppers` | Nobody to sell to |
| Orders and checkout | `apps.orders` | The transaction itself |
| Fulfilment settings | `apps.fulfillment` | A store must say how it hands goods over — collection at minimum |
| Payments | `apps.payments` | Recording that money arrived. The *provider integrations* are separate; recording is not |

**Optional Features** — versioned, signed, installed on entitlement:

| Capability | Feature | Why optional |
|---|---|---|
| Promotions and coupons | `knight-feature-promotions` | A store without them sells at list price, which is a plainer store rather than a broken one |
| Delivery zones and pricing | `knight-feature-delivery` | Collection-only is a complete business; zone pricing is an upgrade |
| Analytics | `knight-feature-analytics-core`, `-reports` | Already built in phase 3.5, and the original example of a capability that measures rather than enables |

## Consequences

**Positive** — the split follows a stated rule, so the next capability has an
answer before somebody argues about it. The base store stays small enough to
reason about, and every Feature is genuinely optional rather than notionally so.
The two Features chosen also exercise the parts of delivery that matter: both
carry migrations, and promotions carries data that must survive being unsold.

**Negative** — orders must reference a promotion that may not be installed. That
is handled the way the .NET modules already handled it: the order stores a
**snapshot** of the discount it received, not a foreign key to the promotion.
An order priced with a coupon stays readable, and stays correct, after the
promotions Feature is uninstalled and its tables are gone. This is a real
constraint on the port and the reason `OrderPromotionSnapshot` exists at all.

**Also** — payments being base while provider integrations are not means the
base store can record a payment it did not itself take. That matches what
KNIGHT does one level up (`risks.md` R14) and keeps a store useful to a business
reconciling bank transfers by hand.
