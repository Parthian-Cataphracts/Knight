# The KNIGHT Feature Catalogue

What KNIGHT sells, what it delivers, and how the two stay the same list.

Read [`feature-delivery.md`](feature-delivery.md) first for the machinery, and
[`feature-authoring.md`](feature-authoring.md) for how to write one. This
document is the **product surface**: which capabilities exist, which are base
and which are sold, what depends on what, and the order they get built in.

---

## 1. The three tiers

| Tier | What the customer gets |
|---|---|
| **Basic** | The base store. A working shop on shared hosting |
| **Custom** | The base store plus whichever optional Features they switch on |
| **Professional** | Every capability KNIGHT sells, on a machine of their own |

The principle behind the split, and the one that decides every future
argument about it:

> **Do not monetise basic functionality by artificially removing essential
> capabilities. Monetise sophistication.**

A merchant must be able to run a legitimate small shop or restaurant on Basic
without buying anything. The reason to leave Basic is that KNIGHT helps them
sell more, retain more customers, operate more efficiently or understand their
business better — never that Basic was made unusable on purpose.

---

## 2. The base store

Shipped in the image, present on every store on every plan, never entitled and
never sold. The rule that puts a capability here is in
[`adr/0024`](adr/0024-base-store-versus-optional-feature.md).

| Slug | Capability | Django app |
|---|---|---|
| `catalog` | Products or menu items, categories, variants, modifiers, prices, availability | `apps.catalog` |
| `accounts` | Shopper identity, guest checkout, address book | `apps.shoppers` |
| `orders` | Cart, checkout, order lifecycle, cancellation, refund state | `apps.orders` |
| `payments` | Recording that money arrived, against a provider abstraction | `apps.payments` |
| `shipping` | Delivery and collection modes, methods, zones and fees, fulfilment status | `apps.fulfillment` |
| `promotions` | Coupon codes, percentage and fixed discounts, validity, minimum order | `apps.promotions` |
| `storefront` | The shopper-facing journey | `apps.shop` |

Transactional notifications — order and payment confirmation, cancellation,
fulfilment and password reset — are in the image as `apps.notifications`. They
have no catalogue identity because there is nothing to sell or entitle: every
store sends them, and marketing automation is a separate Feature that does a
different thing.

These appear in the catalogue as `isOptional: false` Features. They are
identities for plan composition and nothing more: there is no artifact, no
entitlement and no install job behind them.

---

## 3. The optional catalogue

One slug names both the sold capability and the deployable package
([`adr/0029`](adr/0029-one-slug-for-the-catalogue-and-the-package.md)). Prices
are the seeded list prices in EUR per month and are edited through the API, not
in code.

### Built and sellable today

| Slug | Name | Category | Price | Package |
|---|---|---|---|---|
| `analytics-core` | Analytics Core | Insight | 29 | `features/knight-feature-analytics-core` (1.1.0) |
| `analytics-reports` | Analytics Reports | Insight | 19 | `features/knight-feature-analytics-reports` |
| `advanced-promotions` | Advanced Promotions | Growth | 39 | `features/knight-feature-promotions` (2.0.0) |
| `reviews-ratings` | Reviews and Ratings | Growth | 19 | `features/knight-feature-reviews-ratings` |
| `advanced-search` | Advanced Search | Growth | 29 | `features/knight-feature-advanced-search` |
| `customer-segmentation` | Customer Segmentation | Insight | 29 | `features/knight-feature-customer-segmentation` |
| `log-shipping` | Log shipping | Insight | 19 | — none; see below |

`log-shipping` is the one capability with no package. It is enforced by
KNIGHT's own ingest endpoint (`IngestionService.LogShippingFeatureSlug`), which
refuses a log batch from a store whose customer is not entitled. That still is
not a flag in the sense [`adr/0014`](adr/0014-features-as-deployable-packages.md)
forbids: it is entitled, refused server-side when unentitled, and audited. It
simply has nothing to install, because the code that does the work is KNIGHT's.

`delivery-zones` is **withdrawn**. Shipping is base, so phase 12 folded the
package into `apps.fulfillment` and removed both the package and its catalogue
identity.

### On the roadmap

Seeded as Draft: they exist in the catalogue and cannot be sold, because a
Draft Feature fails `Feature.CanBeEntitled`. Each joins its plans in the release
that publishes its package.

| Slug | Name | Category | Price | Depends on |
|---|---|---|---|---|
| `loyalty-rewards` | Loyalty and Rewards | Growth | 39 | base only |
| `gift-cards` | Gift Cards and Store Credit | Revenue | 39 | base only |
| `marketing-automation` | Marketing Automation | Growth | 79 | `customer-segmentation` |
| `ai-reports` | AI Reports | Insight | 149 | `analytics-core` |
| `advanced-inventory` | Advanced Inventory | Operations | 59 | base only |
| `restaurant-operations` | Restaurant Operations | Operations | 79 | base only |
| `multi-location` | Multi-Location | Operations | 99 | base only |
| `subscriptions` | Subscriptions and Recurring Orders | Revenue | 69 | base only |
| `external-marketplaces` | Marketplace and Delivery Integrations | Integrations | 99 | `advanced-inventory` (optional) |

`ai-reports` is the only Feature that **requires dedicated infrastructure**: it
runs model inference on its own workers and its cost per customer is not
something a shared machine can bound. Entitling it to a customer on shared
hosting is refused rather than sold and discovered later. Whether a Feature
needs dedicated infrastructure is fixed at publication and the aggregate
refuses to change it afterwards, so it is set correctly in the seed or not at
all.

---

## 4. Dependencies

The rule that keeps this a graph rather than a knot:

> **An optional Feature may depend on the base store and on foundational
> optional Features. Nothing base ever depends on anything optional.**

```
base store (catalog, orders, accounts, payments, shipping, promotions)
│
├── reviews-ratings          advanced-search        loyalty-rewards
├── gift-cards               advanced-inventory     restaurant-operations
├── multi-location           subscriptions
│
└── analytics-core ──┬── analytics-reports        (>=1.0.0,<2.0.0)
                     ├── ai-reports
                     └── customer-segmentation    (>=1.1.0,<2.0.0)
                                │
                                └── marketing-automation

advanced-inventory ── external-marketplaces
```

Two things this graph is careful about:

**Dependencies are declared against a service surface, not a schema.** A
manifest says `{ slug: analytics-core, version: ">=1.0.0,<2.0.0" }` and the
dependent imports that Feature's `services.py`. That is what makes the range
honest — `analytics-core` can change how it stores events without breaking
`analytics-reports`, which is exactly why `analytics-core/services.py` says it
is the published surface.

**The resolver refuses rather than guesses.** A cycle, a range nothing
satisfies, or two Features wanting incompatible versions of a third produces no
job and an explanation. It never downgrades an installed Feature to satisfy a
dependency, and a Feature another installed Feature depends on cannot be
uninstalled until the dependent goes first.

`customer-segmentation` was listed as *optionally* depending on analytics while
it was a roadmap entry. Building it settled the question the other way, and the
reason is worth keeping: a Feature may not import store business code, so
segmentation cannot read the order table at all. The analytics event stream is
its only possible source, which makes the dependency mandatory — and the lower
bound `>=1.1.0` equally so, because 1.0.x cannot group events by subject. This
is the pair that exercises the resolver end to end
([`phase-13-verification.md`](phase-13-verification.md)).

---

## 5. Adding a Feature to KNIGHT

The end-to-end procedure. Steps 1–5 are the same for every Feature; the
authoring detail is in [`feature-authoring.md`](feature-authoring.md).

### 1. Decide it is a Feature at all

Apply the rule in [`adr/0024`](adr/0024-base-store-versus-optional-feature.md).
If a store is *broken* without it, it belongs in the base image and this
procedure is the wrong one.

### 2. Give it its identity in the catalogue

Add it to
[`commercial-catalogue.json`](../backend/src/Knight.Infrastructure/ControlPlane/Seed/commercial-catalogue.json)
with `publish: false`. It is now a Draft identity: visible, unsellable, and —
crucially — **resolvable by slug**, which is what lets a version be published
against it later.

Do not add it to any plan yet. A Draft Feature cannot be entitled, so a plan
entry would put a toggle on the Custom screen that refuses every time somebody
uses it.

### 3. Build the package

```
features/knight-feature-<name>/
├── knight_manifest.yaml      slug: <name>   — the same slug as step 2
├── pyproject.toml            name: knight-feature-<name>
└── knight_feature_<name>/
```

The manifest's `slug` is the KNIGHT identity and the Python distribution name
keeps its prefix; `knight_package.py` derives the artifact name from the former
and the source directory from `django.installed_app`, so the two never have to
match ([`adr/0029`](adr/0029-one-slug-for-the-catalogue-and-the-package.md)).

Validate before writing code:

```bash
python features/tools/knight_package.py validate features/knight-feature-<name> --base-url http://localhost:5008 --token "$KNIGHT_TOKEN"
```

### 4. Publish it

```bash
KNIGHT_TOKEN=... KNIGHT_ARTIFACT_ROOT=./artifacts python features/tools/knight_package.py publish features/knight-feature-<name>
```

This builds, hashes, signs, uploads, registers and publishes the version.
KNIGHT re-hashes the artifact and verifies the signature against a key it
already trusts; one it cannot verify never becomes installable. A published
version is immutable — fixing a bad release means publishing a new version and
yanking the old one.

### 5. Make it sellable

Two separate acts, deliberately:

- Publish the **Feature identity** (`publish: true` in the seed, or the API) —
  now it can be entitled.
- Add it to the **plans** that should offer it — `isCustomerToggleable: true`
  in Custom, `isIncluded: true` in Professional.

Entitlement and installation stay separate facts throughout. Granting an
entitlement queues delivery; it does not by itself make the capability exist in
a store, and losing an entitlement disables rather than uninstalls.

### 6. Verify it end to end

Not "the tests pass". Per the project rules: install it into a real store
through the dashboard against a running API, walk the screens it touches, and
roll it back. The three things that only bite in delivery:

1. Migrations actually reverse if you declared `reversible: true` — run
   `migrate <app_label> zero` and back.
2. The health check fails when it should. One that always passes turns a failed
   install into a silent one.
3. The store starts without the Feature. A store that cannot boot when an
   optional Feature is absent is a Feature that has reached into the base store.

---

## 6. Where this stands

The delivery engine is complete and exercised: registry, manifest validation,
dependency resolution, signed artifacts, typed jobs, agent execution,
migrations, rollback, staged rollouts. What was missing was not machinery.

Until 2026-08-25 the commercial catalogue and the package registry used
different names for everything, so publishing any real package against a freshly
seeded KNIGHT failed with `No feature is registered with slug '…'`. That is
fixed ([`adr/0029`](adr/0029-one-slug-for-the-catalogue-and-the-package.md)),
and the four packages in `features/` now resolve against the identities the seed
creates.

Phase 12 then closed the base-store boundary: coupons, shipping and
transactional notifications are in the image, `advanced-promotions` 2.0.0 keeps
only the sophistication, and `delivery-zones` is gone. What that cost, and what
running it found, is in [`phase-12-verification.md`](phase-12-verification.md).

Phase 13 then put three real Features through the delivery engine and found
that it had never sent a store the names it needs to load a package — so no
Feature's URLs had ever been mounted, including an endpoint that had shipped
since phase 3.5. That is fixed and pinned by tests both sides
([`phase-13-verification.md`](phase-13-verification.md)).

The remaining work is the catalogue itself: nine Features that are Draft
identities with no package behind them. The order is in
[`../TODO.md`](../TODO.md), phases 14 to 17, and it runs low-risk first:
contained migrations and no external services before anything that touches
money, background workers or a third-party API.
