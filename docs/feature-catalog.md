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
| `advanced-search` | Advanced Search | Growth | 29 | `features/knight-feature-advanced-search` (1.1.0) |
| `customer-segmentation` | Customer Segmentation | Insight | 29 | `features/knight-feature-customer-segmentation` |
| `loyalty-rewards` | Loyalty and Rewards | Growth | 39 | `features/knight-feature-loyalty-rewards` |
| `gift-cards` | Gift Cards and Store Credit | Revenue | 39 | `features/knight-feature-gift-cards` |
| `marketing-automation` | Marketing Automation | Growth | 79 | `features/knight-feature-marketing-automation` |
| `ai-reports` | AI Reports | Insight | 149 | `features/knight-feature-ai-reports` |
| `advanced-inventory` | Advanced Inventory | Operations | 59 | `features/knight-feature-advanced-inventory` |
| `restaurant-operations` | Restaurant Operations | Operations | 79 | `features/knight-feature-restaurant-operations` |
| `multi-location` | Multi-Location | Operations | 99 | `features/knight-feature-multi-location` |
| `subscriptions` | Subscriptions and Recurring Orders | Revenue | 69 | `features/knight-feature-subscriptions` |
| `external-marketplaces` | Marketplace and Delivery Integrations | Integrations | 99 | `features/knight-feature-external-marketplaces` |
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

**Nothing.** Phase 17 published the last two, so every Feature in this catalogue
has a package behind it and the table above is the whole of it. The section is
kept rather than deleted because the next Feature anybody adds goes here first:
a Draft identity exists in the catalogue and cannot be sold, because a Draft
Feature fails `Feature.CanBeEntitled`, and it joins its plans in the release that
publishes its package.

`advanced-search` is the Feature that **requires a particular database** most
visibly. Its index is a `tsvector` column and a GIN index, so it declares
`compatibility.database: postgresql` and the resolver refuses a store on any
other engine before an install rather than after a failed health check. That key
was a comment until phase 14, because the schema had nowhere to put it.

Since 1.1.0 it also declares a **database extension**, `pg_trgm`, which is what
its typo tolerance is built on. Extensions are declared in
`migrations.extensions`, created by the store before the Feature's migrations
run, and never dropped again — an extension is shared with the store and with
every other Feature in the same database, so a rollback that removed one could
break a Feature it has never heard of
([`adr/0031`](adr/0031-database-extensions-are-declared-not-migrated.md)). The
list KNIGHT accepts is closed, and declaring anything on it requires
`compatibility.database: postgresql`.

`ai-reports` is the only Feature that **requires dedicated infrastructure**, and
the reason is cost rather than data: its per-customer spend is not something a
shared machine can bound. Entitling it to a customer on shared hosting is refused
rather than sold and discovered later, and it is the one Feature that appears in
Professional alone. Whether a Feature needs dedicated infrastructure is fixed at
publication and the aggregate refuses to change it afterwards, so it is set
correctly in the seed or not at all.

What its data does is a separate decision, and a stricter one: only aggregates
computed by KNIGHT's own arithmetic may reach a model provider, by allow-list
([`adr/0030`](adr/0030-what-store-data-may-reach-a-model-provider.md)). Its
findings are computed locally and are correct with no provider configured at
all — the model only narrates them.

---

## 3.1 Bundles

A bundle is a **plan composition**, never a package. The same Features at a price
that suits a shape of business — and the moment one becomes a fifteenth package
there are two things to build, test and deliver for one thing to sell.

| Plan | Price | What it adds to the base store |
|---|---|---|
| `growth` | 179 | `analytics-core`, `analytics-reports`, `advanced-promotions`, `reviews-ratings`, `advanced-search`, `customer-segmentation` |
| `retention` | 199 | `analytics-core`, `customer-segmentation`, `loyalty-rewards`, `gift-cards`, `marketing-automation` |

Growth sells what a shop uses to find customers; Retention sells what it uses on
the ones it has. `customer-segmentation` is in both deliberately — it is the
Feature that makes the others worth having, and a bundle without it would be
cheaper and worse.

Like every plan, a bundle lists only **published** Features, so both grow as the
catalogue does — `marketing-automation` joined Retention in phase 15.

Two more are described in the strategy and not yet sellable, because the Features
in them are still Draft: Operations (`advanced-inventory`,
`restaurant-operations`, `multi-location`) and Intelligence (`analytics-core`,
`customer-segmentation`, `ai-reports`).

---

## 3.2 Scheduled work

A Feature that needs something to happen without anybody asking declares it in
its manifest:

```yaml
workers:
  - name: expire-points
    entrypoint: knight_feature_loyalty_rewards.services.expire_stale
    schedule: daily
```

KNIGHT delivers the declaration with the install and the store runs it, so
**installing a Feature installs its schedule**. A worker that has to be wired up
by hand on every store is a worker that does nothing on the stores where
somebody forgot.

`schedule` is one of `hourly`, `daily`, `weekly` — a closed list rather than a
cron expression. A cron string is a parser, a timezone question and a support
surface, and the word travels so the store can decide what it means for its own
timezone. The store runs `manage.py knight_run_workers` on its own cron; that
command decides what is actually due.

Seven Features use it today: `loyalty-rewards` expires points daily,
`ai-reports` writes yesterday's report daily, `marketing-automation` runs
campaigns hourly — hourly because abandoned cart is the one trigger whose delay
is measured in hours — and four declare two each. `advanced-inventory` ends
expired stock holds hourly and sweeps for low stock daily;
`restaurant-operations` ends expired slot holds and releases pre-orders hourly
and closes abandoned table sessions daily; `subscriptions` bills what is due
hourly and retries failed payments daily; `external-marketplaces` flushes its
outbound queue hourly and reconciles daily.

`advanced-inventory` is the one to read for what a worker should and should not
be responsible for. Its hourly expiry is **tidying, not correctness**: an expired
hold has already stopped counting against what may be sold, because `available()`
excludes it by time rather than by state. A store whose cron never runs still
sells the right things; it just accumulates dead reservation rows. Arithmetic
that depended on a job having run would be arithmetic at the mercy of a crontab.

`subscriptions` is the one Feature whose worker is **not** merely tidying, and
it is worth reading against the others. A stock hold that has expired has already
stopped counting; a pickup slot that has passed is already unbookable; but a
period nobody billed is money a merchant does not have, and no derived reading
makes it right in the meantime. What protects a shopper from the worker running
twice is therefore not the schedule but a unique index on the period — which is
the general lesson: when a worker has to be correct rather than tidy, the
correctness belongs in a constraint.

`node-conformance` declares one too, and it is the only worker in the repository
that is not a Django callable: a node entrypoint is `module#exportedName` rather
than a dotted path. It is not in the commercial catalogue — it exists so the
`node` runtime is demonstrated rather than declared
([`adr/0032`](adr/0032-a-feature-declares-its-runtime.md) §4) — but the worker
declaration is real, and it is what proves the schedule contract is not Django's
either.

`multi-location` declares **no workers at all**, and says so with an empty list
rather than by omitting the key. Nothing it does happens on a clock: a branch is
described when a merchant describes it, a rota changes when somebody changes it,
and an order is routed at the moment it is placed. A worker declared for tidiness
would be a cron entry doing nothing on every store that installs it.

`restaurant-operations` follows the same rule twice over, which is what makes it
a rule rather than a habit. Its slot arithmetic excludes an expired hold by time,
and its kitchen board shows a pre-order by time; a restaurant whose cron has
never run therefore quotes honest pickup times and cooks the right food, and both
workers exist only so that the *stored* state matches what the screens already
show — which is what every report afterwards reads.

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
├── multi-location           subscriptions          external-marketplaces
│
└── analytics-core ──┬── analytics-reports        (>=1.0.0,<2.0.0)
                     ├── ai-reports
                     └── customer-segmentation    (>=1.1.0,<2.0.0)
                                │
                                └── marketing-automation    (>=1.0.0,<2.0.0)
```

`external-marketplaces` was expected to depend on `advanced-inventory` and does
not, which is worth recording because the expectation was reasonable. It can tell
a POS what stock a shop has, and that is much more useful with the inventory
Feature installed — but an integration that refused to install without it could
not send orders to an accounting system, which needs no stock at all. The
resolver is not asked to enforce something the code does not require.

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

Phase 14 added the two Features that keep a running balance, and both are built
on one rule: the ledger is the truth and the balance is derived from it. It also
made `compatibility.database` a real constraint the resolver enforces, tested the
uninstall guard, and turned the first two bundles into plans
([`phase-14-verification.md`](phase-14-verification.md)).

Phase 15 gave the catalogue scheduled work — declared in a manifest, delivered
with an install — and then the two Features that need it. `marketing-automation`
is the first to use a third-party credential, and `ai-reports` the first that can
spend money per use, so it arrived with a cap that refuses before it costs
anything and with [`adr/0030`](adr/0030-what-store-data-may-reach-a-model-provider.md)
settling what may leave a store at all
([`phase-15-verification.md`](phase-15-verification.md)).

Phase 17 finished it. `subscriptions` is the first Feature whose worst failure is
charging somebody who did not owe it, and it is arranged entirely around one
unique index — a period is opened before it is charged and numbered per
subscription, so a cron that fires twice, a webhook delivered twice and an
operator running the worker by hand all end at the same constraint.
`external-marketplaces` is the one with the most third-party surface, and its
whole design is that everything crossing the boundary is a row: written before it
is attempted, keyed on the *partner's* event id so a redelivery costs nothing,
retried with widening gaps and then abandoned in a state a person can act on.

The phase also answered R26, which had been the oldest open question in
[`risks.md`](risks.md): a Feature may now be published for a store that is not
Django ([`adr/0032`](adr/0032-a-feature-declares-its-runtime.md)), and
[`../stores/node-reference-store`](../stores/node-reference-store) proves it by
taking delivery of a signed artifact in CI
([`phase-17-verification.md`](phase-17-verification.md)).

Phase 16 finished the operational half of the catalogue: `advanced-inventory`,
then `restaurant-operations`, then `multi-location` — held back since phase 9 on
the grounds that it reshapes data other Features already own. It does not, and
the reason is worth keeping: `advanced-inventory` and `restaurant-operations`
each carried a `location` column from their own 1.0, documented at the time as
the column `multi-location` would name. A Feature owns only its own tables, so
this one *could not* have added those columns later — and that constraint is what
removed the risk rather than what created it. Installing it migrates nobody's
rows, uninstalling it loses no operational data, and a merchant can name one
branch this week and another next month
([`phase-16-verification.md`](phase-16-verification.md)).

There is no remaining catalogue work. Sixteen sellable Features, all with a
package behind them, in five plans.
