# Phase 14 — how it was verified, and what verifying it found

Phase 14 built the two Features that keep a running balance, closed the two gaps
phase 13 left open, and turned the first bundles into plan compositions.

The interesting part is the balances. `loyalty-rewards` owes customers points and
`gift-cards` owes them money, and both are built on one rule that everything else
follows from: **the ledger is the truth and the balance is derived from it.**

Running it found four things. Two were correctness bugs in code that looked
right, one was a route quietly serving the wrong view, and one was an
entitlement check gating on a slug that no longer exists.

---

## 1. What was built

| Slug | What it is |
|---|---|
| `loyalty-rewards` | Points earned in lots with their own expiry, spent oldest-first, tiers on lifetime points, and a ledger nothing edits |
| `gift-cards` | Gift cards spendable across several orders, store credit per customer, and a money ledger with no balance column anywhere |

Plus the two carried items, and the bundles:

- **`compatibility.database`** in the manifest, parsed, validated against a
  closed list, and **enforced by the resolver**. `advanced-search` declares
  `postgresql` and is now refused before an install rather than after a failed
  health check.
- **The uninstall guard is tested.** Three integration tests: a dependency
  cannot be removed while something needs it, the dependent itself can be, and
  once it is gone the dependency follows.
- **Growth and Retention are plans, not packages.** Bundling is a commercial
  act; the moment it becomes a fifteenth package there are two things to build,
  test and deliver for one thing to sell.

---

## 2. What verifying it found

### An `IntegrityError` poisons the transaction it happens in

Both ledgers use a unique constraint for idempotency — a retried checkout must
not earn twice or spend a card twice — and both caught `IntegrityError` and then
reported the current balance.

That cannot work on PostgreSQL. A failed statement marks the whole transaction
broken, so the `except` branch could not run a single query: instead of
reporting a harmless duplicate it raised `TransactionManagementError`, and the
retried checkout failed. Every insert that can collide is now wrapped in its own
savepoint, and both Features have a test named for the failure.

### `FOR UPDATE` cannot be applied to the nullable side of an outer join

`loyalty-rewards` locked the account row with
`select_for_update().select_related("tier")`. `tier` is nullable, so Django
emits a LEFT OUTER JOIN and PostgreSQL refuses outright. The lock is
`of=("self",)` now — the account row and nothing else, which is all that was
ever contended.

Neither of these is visible by reading the code. Both appeared on the first real
call against a real database.

### A Feature's route was silently shadowed by the store's own

`loyalty-rewards` mounted at `loyalty/`, and the reference store already serves
`loyalty/` from its storefront demo. The loader adds Feature URLs **last**, on
purpose — a delivered package must not be able to take over a route the shop
already serves — so the Feature's page answered the shop's view instead. It
returned `402 not entitled` for a Feature that was installed and working.

The direction is right and the silence was not. Three changes:

- The Feature moved to `loyalty-rewards/`, matching its slug — which is also the
  loader's own default when a manifest declares no prefix, and the shape least
  likely to collide with anything. `gift-cards` follows the same rule.
- The loader is now handed the routes the store already serves and **logs an
  error** naming the Feature, the prefix and the consequence. The store still
  wins; it is no longer quiet about it.
- The store's own urlconf passes its patterns in.

This is the same failure class as phase 13's: install succeeds, health check
passes, and a page does not work. It was found the same way — by opening it.

### The storefront demo gated on slugs that no longer exist

`apps/shop/views.py` demonstrates server-side entitlement enforcement, and it
was checking `loyalty` and `analytics`. Those slugs stopped existing when
[`adr/0029`](adr/0029-one-slug-for-the-catalogue-and-the-package.md) gave every
Feature one slug in phase 12. The demonstration was refusing customers who had
genuinely paid, for a capability the catalogue calls `loyalty-rewards`.

Now `loyalty-rewards` and `analytics-core`.

---

## 3. What the ledgers actually do

Verified against a real database, not only in tests.

**Loyalty** — three lots expiring in 5, 40 and 400 days, redeeming 150:

```
  lot expiring in   5d: 0 of 100 left
  lot expiring in  40d: 150 of 200 left
  lot expiring in 400d: 300 of 300 left
```

Oldest-expiry-first, so what is about to lapse is spent first and the store's
liability falls rather than ageing. `expiringSoon` reported 100 before the
redemption — the number that makes a loyalty programme work, because points
nobody is warned about quietly become a complaint.

The expiry sweep removed exactly the lapsed lot, and running it again removed
nothing. A scheduled job that cannot safely be re-run is a job nobody can retry
after an outage.

**Gift cards** — a 50.00 card:

| Step | Result |
|---|---|
| Spend 20.00 on order 1 | 30.00 left |
| Retry order 1 | duplicate, still 30.00 |
| Ask for 100.00 with 30.00 left | settles 30.00 — partial settlement is the normal case |
| Spend again when empty | refused: "no value left on it" |
| Refund order 2 | 30.00 back, card returns to Active from Depleted |
| Void it | outstanding liability drops 30.00 → 0.00 |
| Spend a voided card | refused: "not active" |

Three refusals, three different messages. A shopper told the wrong one takes the
wrong next step — and getting that right needed a fix: `is_redeemable()`
originally treated a depleted card as inactive, so an empty card was told it had
been voided.

---

## 4. Repeating it

Database up, from the repository root:

```bash
docker compose -f infrastructure/docker/docker-compose.yml up -d
```

In `stores/reference-store`, with the **Python 3.12** virtualenv:

```bash
python -m pip install ../../features/knight-feature-loyalty-rewards ../../features/knight-feature-gift-cards
```

```bash
python manage.py knight_install_local ../../features/knight-feature-*/
```

```bash
python manage.py migrate
```

Start the store and read a balance:

```bash
python manage.py runserver 8010
```

```bash
curl "http://localhost:8010/loyalty-rewards/vera/"
```

```bash
curl "http://localhost:8010/gift-cards/check/?code=<the code>"
```

Expiry is a command, run daily by whatever already runs that store's cron:

```bash
python manage.py knight_expire_loyalty_points --dry-run
```

Then the suites:

```bash
REQUIRE_FEATURE_TESTS=1 python manage.py test
```

---

## 5. Test results

| Suite | Result |
|---|---|
| Store, all Features installed, `REQUIRE_FEATURE_TESTS=1` | **367 passed**, 0 skipped (274 before) |
| Store, no Features installed at all | **367 passed**, 189 skipped |
| Backend unit | **595 passed** (590 before) |
| Backend architecture | 13 passed |
| Backend integration, `REQUIRE_POSTGRES_TESTS=1` | **153 passed** (150 before) |

The 93 new store tests are the two ledgers. A good share of them exist to keep a
future change honest rather than to check today's behaviour: that neither
`Account` nor `GiftCard` has a balance column, that a derived balance equals the
sum of its entries, and that a refund writes rows rather than editing them.

---

## 6. What the manifests now promise, and what they do not

`gift-cards` declares `migrations.reversible: true` **for 1.0.0 only**, and says
why in the manifest itself. Any later migration touching an amount, a status, or
the shape of either ledger is **Class C** whatever its operations look like:
Django can put a column back and cannot put money back, and a rollback that
reports success while leaving a customer's card worth the wrong amount is worse
than one that refuses ([`adr/0016`](adr/0016-feature-migration-and-removal-policy.md)).

`loyalty-rewards` carries the same warning about `points_remaining`.

---

## 7. What is deliberately not covered

**There is no manifest-declared worker, and loyalty expiry needs one.** The
schema has no concept of a scheduled job — `ManifestReader` does not read one and
[`feature-delivery.md`](feature-delivery.md) does not describe one — so
declaring `workers:` would have looked like a guarantee and scheduled nothing,
exactly the trap a `database:` key was before this phase made it real. Expiry is
`manage.py knight_expire_loyalty_points` on the store instead.

**Phase 15 has to build workers properly**, because `marketing-automation`
cannot work without them. It is recorded there rather than faked here.

**Neither ledger is reachable from the dashboard.** These are store-side
capabilities and KNIGHT is not a store's business backend, but it means issuing
a card, voiding one, granting credit and adjusting points are all shell
commands today.

**Concurrency is argued, not proven.** The locks and the constraints are the
right ones and every idempotency path has a test, but nothing here runs two
transactions at once to watch them contend. Doing that honestly needs a test
harness with real parallel connections, and it is worth having before the first
customer sells a gift card.
