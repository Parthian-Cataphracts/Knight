# Phase 12 — how it was verified, and what verifying it found

Phase 12 moved two capabilities out of optional Features and into the base store
([`adr/0024`](adr/0024-base-store-versus-optional-feature.md)). The schema
change is a migration; the rows are not, and the interesting part of this phase
is the rows.

This is the record of running it against a real store with real legacy data,
and the steps to repeat it.

---

## 1. What must be true afterwards

- A store that never had either Feature gains coupons, delivery zones and
  transactional notifications, and nothing about it changes otherwise.
- A store that **had** the promotions Feature keeps every campaign, every
  coupon and — the one that actually matters — every redemption count.
- An order priced before the move still reads correctly afterwards.
- `advanced-promotions` 2.0.0 prices rules the base store cannot, and the store
  keeps selling when it is absent.

---

## 2. Repeating it

Bring the database up, from the repository root:

```bash
docker compose -f infrastructure/docker/docker-compose.yml up -d
```

Then, in `stores/reference-store` with its virtualenv active:

```bash
python manage.py migrate
```

That applies `promotions.0001_initial`, `fulfillment.0002_…` (the
`delivery_accepting_orders` switch and the `DeliveryZone` table) and
`notifications.0001_initial`.

**On a store that had the old Features, absorb the rows before anything else:**

```bash
python manage.py knight_absorb_promotions --dry-run
```

```bash
python manage.py knight_absorb_delivery_zones --dry-run
```

Both report what they would move and write nothing. Drop `--dry-run` to do it.
Add `--enable-delivery` to the second one when the store was delivering through
the Feature but the base store's own delivery switch was never set — otherwise
every zone is in place and refuses to quote, which looks like the absorption
having failed.

**Only then** upgrade the Feature:

```bash
python manage.py migrate knight_promotions
```

That runs `0002_advanced_rules_replace_base_coupons`, which **drops the
promotion, coupon and redemption tables**. Running it before absorbing loses
every campaign on that store. The manifest declares
`migrations.reversible: false` for this reason, so KNIGHT stops with
`ManualInterventionRequired` rather than pretending Django's reverse can put a
customer's campaigns back
([`adr/0016`](adr/0016-feature-migration-and-removal-policy.md)).

Finally, the suite:

```bash
REQUIRE_FEATURE_TESTS=1 python manage.py test
```

---

## 3. What the real run did

Legacy data was seeded into the Feature's own tables first: one promotion
("Ramadan 20%", 20% off, coupon-gated), one coupon (`ramadan20`, limit 100) with
**two redemptions recorded**, and two delivery zones — Central at 30,000 and a
far suburb at 80,000 with its own 400,000 minimum — with the Feature's store
settings paused and carrying a 150,000 default minimum.

Dry run:

```
promotions: 1 moved, 0 already present
coupons: 1 moved, 0 already present
redemptions: 2 moved, 0 already present
Dry run - nothing was written.
zones: 2 moved, 0 already present
Dry run - nothing was written.
```

Base tables afterwards: `base promotions: 0  base zones: 0`. The dry run is
genuinely dry.

The real run moved the same rows. Running both commands a second time:

```
promotions: 0 moved, 1 already present
coupons: 0 moved, 1 already present
zones: 0 moved, 2 already present
```

Idempotent, as an operator who is unsure whether the first run completed needs
it to be.

After the Feature upgrade to 2.0.0, against the absorbed data:

| Checked | Result |
|---|---|
| Coupon still prices after the Feature dropped its tables | `Ramadan 20%` → 20,000 off 100,000 |
| Redemption counts survived | 2 of 100 used |
| Pause switch survived | quotes refused with "The store has paused deliveries" |
| Store-wide minimum survived | Central refused below 150,000 |
| Zone minimum still overrides the store's | Far suburb refused below 400,000 |
| Buy 2 get 1 free, one product, basket of 3 | 50,000 off |
| Same rule, basket of exactly 2 | nothing — the items that earned the reward are not the reward |
| Bundle of two 60,000 items priced at 90,000 | 30,000 off |
| Coupon **and** bundle, stacking off (default) | 30,000 — the better one, not both |
| Same, `stacks = True` | 54,000, named "Ramadan 20% + Meal deal" |

---

## 4. What verifying it found

**Two constraint names collided.** The base store's new tables and the Feature's
existing ones both declared `delivery_zone_active_name_unique` and
`promotions_redemption_once_per_order`. Django refused the migration with
`models.E032` before it ever reached the database.

This is not a naming preference. During the transition **both tables exist at
once** — that is the whole point of absorbing before upgrading — and PostgreSQL
will not hold two constraints of one name. The base store's are now
`fulfillment_zone_active_name_unique` and
`base_promotion_redemption_once_per_order`, and the comment in each model says
why, so nobody tidies them back.

**The absorption commands crashed when run twice across the upgrade.** Run
`knight_absorb_promotions` on a store already upgraded to 2.0.0 and the import
of a model that no longer exists raised `ImportError` at the operator. That is
the single most likely way to meet a transitional command — unsure whether it
already ran — so both commands now recognise the state and say so.

**A zone could quote on a store that does not deliver.** Under the Feature the
two switches lived in different tables and nothing joined them: `delivery_enabled`
in the base store, `is_accepting_orders` in the Feature. A collection-only store
with leftover zones quoted delivery fees. Now `quote()` checks both, and says
which one refused — "This store does not deliver" and "The store has paused
deliveries" lead to different actions.

**An optional Feature failing did not break checkout, and that was observable.**
Midway through the run the base store was calling a 1.0.0 Feature with a 2.0.0
signature. The seam caught it, logged it, and priced the basket from the base
rules:

```
The advanced-promotions Feature failed while pricing; falling back to base rules.
TypeError: price() got an unexpected keyword argument 'lines'
```

That is the designed behaviour — a store whose checkout breaks because an
optional upsell misbehaved has turned a Feature into an outage — but it is the
kind of thing normally only asserted in a test. It happened for real here.

---

## 5. Test results

| Suite | Result |
|---|---|
| Store, `REQUIRE_FEATURE_TESTS=1` | **184 passed**, 0 skipped (156 before this phase) |
| Backend unit | 584 passed |
| Backend architecture | 13 passed |
| Feature packages built from their manifests | 3 of 3 |

The 28 new store tests are the promotion and delivery suites that used to sit
behind a `skipUnless` — they run unconditionally now, which is the point of the
move — plus the notification suite and the absorption commands.

The PostgreSQL-backed **backend** integration suite was not run here and is left
to CI; the store suite above is PostgreSQL-backed and did run.

---

## 6. What is deliberately not covered

The absorption commands cannot be tested end to end from the suite. Moving rows
needs a store with the *legacy* 1.x package installed, and that package no
longer exists in this repository — deleting it is what "withdraw the Feature"
means. What the suite covers is every state an operator can reach afterwards: a
store that never had the Feature, and one that has already been through the
move. The row-moving itself is covered by section 3 above and nothing else,
which is the honest description of a one-way transitional command.
