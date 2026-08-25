# 0024 — What belongs to the base store, and what is an optional Feature

- Status: **Accepted** — revised 2026-08-25, superseding the original decision
  on promotions and delivery
- Date: 2026-08-20, revised 2026-08-25

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

- **Would removing it leave a broken store or a plainer one?** Removing
  buy-X-get-Y leaves a store that runs simpler campaigns. Removing the
  catalogue leaves nothing.
- **Does it carry data the store must keep even when unsold?** A Feature's
  data survives its entitlement being revoked ([`adr/0016`](0016-feature-migration-and-removal-policy.md)),
  which is workable for a campaign history and absurd for the order table.

### The revision, and why the rule did not change

The original decision put **promotions** and **delivery zones** on the Feature
side. The commercial catalogue review of 2026-08-25 moved both into the base
store. The rule above is unchanged; what changed is the reading of it.

The original argument was that a store selling at list price is plainer rather
than broken, and that collection-only is a complete business. Both are true in
the abstract and neither survives contact with the market KNIGHT sells into. A
small shop or restaurant that cannot issue a discount code, and cannot charge
differently for delivering to the next town than to the next street, is not
running a plainer business — it is missing table stakes that every competing
platform includes, and it will not buy the plan. Charging for them monetises a
deficiency rather than sophistication, which is the one thing the catalogue
strategy forbids:

> Do not monetize basic functionality by artificially removing essential
> capabilities. Monetize sophistication.

The sophistication is real and remains sold: buy X get Y, bundles, scheduled
campaigns and stacking rules are a different capability from a percentage-off
coupon, and they are what `advanced-promotions` is.

## Decision

**Base store** — shipped in the image, present on every store, never entitled:

| Capability | Ported as | Why base |
|---|---|---|
| Catalogue | `apps.catalog` | Nothing to sell without it |
| Shoppers | `apps.shoppers` | Nobody to sell to |
| Orders and checkout | `apps.orders` | The transaction itself |
| Payments | `apps.payments` | Recording that money arrived. The *provider integrations* are separate; recording is not |
| Shipping and fulfilment | `apps.fulfillment` | A store must say how it hands goods over, and what it charges to do so |
| Coupons and discounts | `apps.promotions` | A shop that cannot discount cannot run a normal promotion, and every competing platform includes it |
| Storefront | `apps.shop` | The shopper-facing journey |

**Optional Features** — versioned, signed, installed on entitlement:

| Capability | Feature | Why optional |
|---|---|---|
| Advanced promotions | `advanced-promotions` | Buy X get Y, bundles, scheduling and stacking are merchandising sophistication, not the ability to discount |
| Analytics | `analytics-core`, `analytics-reports` | Already built in phase 3.5, and the original example of a capability that measures rather than enables |
| Everything in the catalogue | [`feature-catalog.md`](../feature-catalog.md) | The full commercial surface and its dependency graph |

`delivery-zones` is **deprecated**: it is a Feature today because the original
decision made it one, and phase 12 folds it into `apps.fulfillment`. It stays
installable until then so no store loses its zones mid-flight.

## Consequences

**Positive** — the split follows a stated rule, so the next capability has an
answer before somebody argues about it. Basic is now a plan a real small
business can run on, which is what makes the upgrade argument honest: a customer
leaves Basic because KNIGHT helps them sell more, not because Basic was made
unusable on purpose.

**Negative — and this is the cost of the revision.** Two Features that exist,
are published and are installed must move into the base image:

- `advanced-promotions` currently ships the base coupon rules. Phase 12 moves
  those rules into `apps.promotions` and grows what remains into the advanced
  set. Every store with the Feature installed needs its promotion rows moved
  from the Feature's tables into the base store's, which is a data migration on
  a live schema, not a `CreateModel`.
- `delivery-zones` moves wholesale into `apps.fulfillment`, with the same
  problem for its zone and fee rows.

This is exactly the migration cost the original ADR warned about when it said
moving a capability across the line later means migrating data on every store
that has it. It is affordable **only** because the affected stores are
development installs with `sha256:local-development` digests and no customer
data. Doing it after the first paying customer would have been considerably
worse, which is the argument for doing it in phase 12 rather than deferring.

**Also — the snapshot rule survives and matters more than before.** Orders
reference a promotion that may not be installed, so the order stores a
**snapshot** of the discount it received, not a foreign key
(`OrderPromotionSnapshot`). That was written to survive uninstalling the
promotions Feature; it now also has to survive the rules moving between the
Feature and the base store. An order priced with a coupon in 2026 stays readable
and stays correct whichever side of the line the rule that produced it now lives
on.

**Also** — payments being base while provider integrations are not means the
base store can record a payment it did not itself take. That matches what
KNIGHT does one level up (`risks.md` R14) and keeps a store useful to a business
reconciling bank transfers by hand.
