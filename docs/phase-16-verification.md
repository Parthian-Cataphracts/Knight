# Phase 16 — how it was verified, and what verifying it found

Phase 16 finished the operational half of the catalogue. Its exit criterion was
**KNIGHT is credible for a merchant with real operations behind the shop**: not a
shop that lists things and takes money, but one with stock on shelves, a kitchen
with a pass, and possibly more than one address.

Three Features, in deliberate order — the safest first and the one everybody was
afraid of last — plus the amendment that unblocked the first two.

---

## 1. What was built

| | |
|---|---|
| [`adr/0031`](adr/0031-database-extensions-are-declared-not-migrated.md) | How a `CREATE EXTENSION` is classified, once and for both callers. Declared in the manifest, created before migrations, never dropped |
| **`advanced-search` 1.1.0** | Typo tolerance on `pg_trgm`, the first consumer of that rule |
| **`advanced-inventory`** | Stock as an append-only ledger: movements, reservations, suppliers, purchase orders, low-stock alerts |
| **`restaurant-operations`** | Tables and sessions, kitchen tickets with states finer than an order's, preparation times per dish and per station, and pickup times that are booked rather than displayed |
| **`multi-location`** | Branches with their own addresses, timezones and opening hours, staff rotas, per-branch menu exceptions, and order routing decided once and written down |

The catalogue is now **fourteen sellable Features** and two Draft identities,
`subscriptions` and `external-marketplaces`, which are phase 17.

---

## 2. The claim `multi-location` was held back for

Since phase 9 this Feature has been described as the one that "reshapes data
other Features already own", and scheduled last for that reason. Building it
showed the premise was wrong, and the reason is the delivery model rather than
luck.

A Feature owns only its own tables. `multi-location` therefore *cannot* add a
column to a stock movement or a kitchen ticket — and both of those Features
carried a `location` column from their own 1.0, each documented at the time as
the column this one would name. So there was nothing to reshape:

- installing it **migrates nobody's rows**. Its `0001_initial` is eight
  `CreateModel`s, all in its own app, with no `AlterField`, no `RunPython` and
  nothing outside `knight_locations_*`;
- uninstalling it **loses no operational data**. Every stamp stays where it is
  and merely stops having a name attached;
- and a merchant can adopt it **a branch at a time**, because `describe()`
  returns `None` for a code nobody has named rather than raising. That is the
  state every code in the store is in on the morning of the install.

`TheCodesTheOtherFeaturesAlreadyStampedTests` is the demonstration, and it runs
with all three Features installed: stock is received at `CAMDEN` before anybody
has described `CAMDEN`, the branch is then described, and every movement row is
compared before and after — same rows, same locations, same quantities.

The constraint that looked like the risk is what removed it.

---

## 3. The three claims worth testing in `restaurant-operations`

**A promise is the longest dish plus the queue, never the sum of the dishes.** A
kitchen cooks in parallel. Quoting the sum is wrong twice: too slow when it
promises, and too early when the food lands. Quantity multiplies the *load* on
the kitchen and not the wall clock, because a second portion goes in the same
pan. An unprofiled dish is assumed to take the configured default rather than
zero — zero would make the kitchen look empty at exactly the moment it is
handling something nobody has measured.

**Capacity is a promise, not a display.** A pickup time shown but not taken is a
time two people are given, and one of them has already left the house. What is
left in a slot is derived from its bookings; there is no counter to drift.

**Holds stop counting by time, not by state.** A restaurant whose cron has never
run still quotes honest slots and still sees the right tickets on the board, and
both workers are tidying rather than correctness — the rule `advanced-inventory`
set for expiring stock holds, applied twice more.

### The concurrency demonstration, again

`ConcurrentBookingTests` races two threads on two connections for the last space
in a slot. One books it, one is refused with `NoCapacity`, and one row exists
afterwards.

Removing the `select_for_update` from `book()` and re-running it three times
produced two double-bookings and one accidental pass, which is exactly the shape
of the bug: it is not that the unlocked version always breaks, it is that it
breaks whenever the timing is unlucky and looks fine the rest of the time.

---

## 4. What verifying it found

Four real defects. Two of them were in the same place — the YAML readers that
parse a manifest where PyYAML is not installed — and neither could have been
found by running anything on a developer's machine.

### The fallback manifest reader was wrong for the fourth time

`restaurant-operations` declares `extensions: []` — nothing in it is matched
fuzzily, so it needs no database extension and says so. The store's
no-PyYAML fallback reader returned that as the **string `"[]"`**: truthy,
iterable, and one character away from being a list containing `[`.

It was caught by the differential test added in phase 15, which parses every
manifest in the repository both ways and requires the same document. That test
has now caught three of the four bugs in that reader — the fourth being the
phase-13 one it was written in response to — which is the argument for it. Fixed
with the inline-sequence case it never had, and pinned.

### The packaging tool could not read a manifest with prose in it

Found by CI rather than locally, which is the point: `knight_package.py` uses
PyYAML when it is installed and its own reader when it is not, and it is not on a
runner. That reader strips a `#` comment unless the line contains an odd number
of double quotes — a heuristic meant to protect a `#` inside a quoted value —
so a comment ending in a quoted phrase was read as content and refused for having
no colon in it. Every prose comment in this repository's manifests is one quoted
phrase away from that, and `restaurant-operations` happened to be the first to
land on it.

A whole-line comment is now a comment before any heuristic is consulted.

The deeper problem was that this reader had no test at all while its twin in the
store had a good one. It now has `knight_package.py selftest`, which reads every
manifest in the repository and compares against PyYAML where PyYAML exists — and
CI runs it before it builds anything, so the next bug in it fails on a check that
names the file rather than on a build that stops halfway through the catalogue.

### Every list screen in the dashboard showed one page and called it the whole thing

`useCollection` read `response.items` and dropped the paging. Every screen built
on it filters, counts and renders the whole collection client-side, so the hook
did not return a short list — it returned a wrong one.

Phase 16 is what made it visible: the catalogue passed the default page size of
twenty-five, so the Features screen showed twenty-five of twenty-nine features
and its "Draft: 1" tab was counting a page rather than a catalogue, with
`subscriptions` invisible behind it. It has been latent since the dashboard was
built, and no test could see it, because a fixture returns everything on page
one.

The hook now follows `totalPages`, capped at twenty pages. A screen that names
its own page size is saying "the most recent this many" and gets one page — the
notification centre already did, and the audit and log screens now do too, both
being append-only and unbounded.

One iteration of that fix broke the notification centre, and it is worth
recording: appending `pageSize` to a path that already had one does not override
it. The query arrives as `pageSize=50&pageSize=100`, model binding reads the
single value `"50,100"`, and the endpoint answers 500.

### The ticket number rolled over into a unique column

Not found by the browser or by CI but by reading the two statements next to each
other, which is worth recording because they were both deliberate and they
contradicted. The kitchen ticket number rolls over at four digits — the entire
reason it exists beside the order number is that it is short enough to shout —
and the column was also unique across all history. A restaurant open all year
would eventually be handed a number some ticket already had, and get an
`IntegrityError` in the middle of service.

Uniqueness now says what was meant: a partial unique index over the non-terminal
states. Ticket 41 this Tuesday and ticket 41 next month are different tickets and
both are correct; two tickets on the board at once may not share a number,
because that is a number somebody shouts and two people answer to. The counter
skips a number a live ticket holds rather than letting the constraint refuse it,
so the refusal never reaches a member of staff as a database error.

---

## 5. Publishing an existing Draft is an operator act

Worth writing down because it is not a bug and looks like one. The catalogue
seeder publishes a Feature **only when it creates the row**. Flipping
`"publish": true` in `commercial-catalogue.json` therefore has no effect on a
database where the Feature already exists as Draft — every deployment older than
this release.

That is deliberate: publishing is a commercial decision, it has its own endpoint
and its own permission, and a redeploy is not the moment to overrule an operator
who deprecated something. On an existing deployment, `restaurant-operations` and
`multi-location` are published with

```
POST /api/v1/features/{id}/publish
```

or from the Features screen. A fresh database gets them published from the seed.

---

## 6. How to test it

### Bring the whole thing up

```bash
docker compose -f infrastructure/docker/docker-compose.yml up -d
```

```bash
export CONTROL_PLANE_DB_CONNECTION_STRING="Host=127.0.0.1;Port=5433;Database=knight;Username=knight;Password=knight"
```

Migrate and seed the control plane, and create an administrator. It reads the
password twice from stdin and holds `SuperAdmin`, so its first sign-in enrols a
second factor:

```bash
dotnet run --project backend/tools/Knight.Bootstrap -- --control-plane --email phase16@knight.dev
```

```bash
dotnet run --project backend/src/Knight.Api --urls http://localhost:5008
```

```bash
cd frontend/knight-dashboard && npm install && npm run dev
```

`.env.local` must have `VITE_USE_MOCKS=false`, or the screens answer from
fixtures and prove nothing.

### The dashboard

Open `http://localhost:5173`, sign in as `phase16@knight.dev`, and enter the
six-digit code from the authenticator enrolled at first sign-in.

**Features** (`/features`). Expect **29 total, 27 published, 2 draft** against a
fresh seed — and expect those three numbers to match

```bash
docker exec docker-postgres-1 psql -U knight -d knight -c 'select "Status", count(*) from control.features group by 1;'
```

`Restaurant Operations` and `Multi-Location` are Published; `subscriptions` and
`external-marketplaces` are Draft. If the tab counts and the database disagree,
the paging fix in §4 has regressed.

**Plans** (`/plans`). In the entitlement matrix, both new Features read
`— / yes / yes / — / —` across Basic, Custom, Professional, Growth, Retention.
Toggleable rather than included in Professional: they are the only two Features
in the catalogue that assume a *shape* of business, and a shop with no kitchen or
one address should not have screens for either.

### The store, which is where these Features actually live

```bash
cd stores/reference-store
export KNIGHT_FEATURE_ROOT=$PWD/../knight-features
pip install ../../features/knight-feature-restaurant-operations ../../features/knight-feature-multi-location
python manage.py knight_install_local ../../features/knight-feature-*/
python manage.py migrate
python manage.py runserver 0.0.0.0:8000
```

Give it a room, a kitchen and two branches:

```bash
python manage.py shell -c "from knight_feature_multi_location import services as l; l.define_location('CAMDEN', name='Camden Road', city='London'); l.set_default('CAMDEN'); l.define_rule('postal-prefix', pattern='NW1', location='CAMDEN', priority=10)"
```

```bash
python manage.py shell -c "from knight_feature_restaurant_operations import services as r; r.define_station('GRILL', name='Grill', location='CAMDEN'); r.define_prep('BURGER', name='Cheeseburger', station='GRILL', prep_minutes=12, load_units=4); r.define_table('12', name='Table 12', seats=4, location='CAMDEN'); r.seat('12', party_size=3, label='window'); r.open_ticket([{'sku': 'BURGER', 'name': 'Cheeseburger', 'quantity': 2, 'modifications': 'no onions'}], order_number=4471, table='12', location='CAMDEN')"
```

Then the surfaces. The kitchen board, oldest first:

```bash
curl "http://localhost:8000/restaurant/?location=CAMDEN"
```

Expect one ticket, `"state": "queued"`, `"table": "12"`, `"modifications": "no
onions"` carried through from the till, and a `promisedAt` twelve minutes ahead —
twelve, the longest dish, not twenty-four for two of them.

The floor, and the number behind every promise:

```bash
curl "http://localhost:8000/restaurant/floor/?location=CAMDEN"
```

```bash
curl "http://localhost:8000/restaurant/load/?location=CAMDEN"
```

Seating the same table twice must answer **409**, not 400 — a till needs to tell
"the world is not as you assumed" from "your request was wrong" so it can offer
the open session instead:

```bash
curl -i -X POST "http://localhost:8000/restaurant/seat/" -H "Content-Type: application/json" -d '{"table":"12","partySize":2}'
```

Bumping a ticket past a state it cannot reach is 409 for the same reason:

```bash
curl -i -X POST "http://localhost:8000/restaurant/1/advance/" -H "Content-Type: application/json" -d '{"state":"served"}'
```

Lay out a day of pickup slots, then ask for more than one holds. Expect **409**
and a `remainingUnits` the checkout can put in a sentence:

```bash
python manage.py shell -c "from datetime import time; from django.utils import timezone; from knight_feature_restaurant_operations import services as r; r.ensure_slots(timezone.localdate(), opens=time(18,0), closes=time(20,0), location='CAMDEN', capacity_units=8)"
```

```bash
curl "http://localhost:8000/restaurant/slots/?location=CAMDEN&units=3"
```

Booking the same reference twice returns the same booking rather than taking the
slot twice — a retried checkout must not cost a shopper two tables:

```bash
curl -i -X POST "http://localhost:8000/restaurant/book/" -H "Content-Type: application/json" -d '{"startsAt":"<a startsAt from the call above>","reference":"order-9002","units":2,"location":"CAMDEN"}'
```

Routing. The second call returns the **first** decision, unchanged, although the
postcode says otherwise — that is the whole point:

```bash
curl -i -X POST "http://localhost:8000/locations/route/" -H "Content-Type: application/json" -d '{"orderNumber":4471,"postalCode":"NW1 8QP"}'
```

```bash
curl -i -X POST "http://localhost:8000/locations/route/" -H "Content-Type: application/json" -d '{"orderNumber":4471,"postalCode":"W1D 4SB"}'
```

```bash
curl "http://localhost:8000/locations/CAMDEN/"
```

The seams from the store's side, all three safe to run on a shop that has neither
Feature installed — they say so and exit:

```bash
python manage.py knight_sync_prep_times --minutes 12
```

```bash
python manage.py knight_print_kitchen_tickets
```

```bash
python manage.py knight_route_orders
```

Run the last two twice. Neither prints or routes anything a second time.

The declared workers. `restaurant-operations` contributes two and
`multi-location` deliberately contributes none:

```bash
python manage.py knight_run_workers --dry-run
```

And the suites:

```bash
REQUIRE_FEATURE_TESTS=1 python manage.py test
```

```bash
cd backend && REQUIRE_POSTGRES_TESTS=1 dotnet test
```

---

## 7. Test results

| Suite | Result |
|---|---|
| Store, all 13 Features installed, `REQUIRE_FEATURE_TESTS=1` | **684 passed**, 0 skipped (479 before) |
| Store, no Features installed at all | **684 passed**, 467 skipped |
| Backend unit | 626 passed |
| Backend architecture | 13 passed |
| Backend integration, `REQUIRE_POSTGRES_TESTS=1` | 155 passed |
| Dashboard | 9 passed, `tsc --noEmit` clean |

The 205 new store tests are the three Features of this phase and the regression
for the inline-sequence bug in §4. **No backend tests were added**: nothing in
this phase changed KNIGHT's own code, only the catalogue data it seeds. The
backend figures differ from the ones phase 15 reported because that report went
stale between then and now, not because this phase moved them; these are the
numbers `dotnet test` prints today.

---

## 8. What is deliberately not covered

**Neither new Feature has a dashboard screen**, and that is the architecture
rather than an omission. A floor plan, a kitchen display and a branch picker are
a store's business backend, and KNIGHT is not that. A merchant drives these
through their own store's surfaces; what KNIGHT owns is whether the Feature may
be sold, installed and run at all.

**There is no kitchen display client.** `board()` and the endpoints over it are
the contract a display screen would be built against, and building the screen is
a store's work. The polling shape — one indexed query per refresh, filtered by
station — is designed for it and measured by nothing.

**Real-time order updates are still polling.** The TODO named "real-time order
updates" for this Feature; the endpoints support a screen that refreshes every
few seconds and no push channel was added. A kitchen board is one of the few
places where polling every two seconds is genuinely fine, and a SignalR-style
channel between a store and its own staff is the store's to build, not a
Feature's to impose.

**Routing does not know about distance.** Rules match a postcode prefix, a city
or a zone name, and there is no geography in the box although a location carries
its latitude and longitude. A merchant who wants "nearest branch" needs a rule
kind that does not exist yet. That was a deliberate refusal: every expression
language eventually grows a debugger, and a closed list of four rule kinds is
something a manager can reason about at eleven at night.

**A branch's stock is not enforced against its menu.** `multi-location` can say
Soho does not sell the burger and `advanced-inventory` can say there are none
left at Camden, and nothing joins the two into "do not offer this here". That
join belongs to a store's checkout, which is the only party that knows what it is
about to sell — and inventing it inside either Feature would be one Feature
reaching into another's tables, which is the rule the whole catalogue rests on.

**Concurrency is proven for booking and for stock, and argued everywhere else.**
`book()` and `reserve()` have real two-connection races. The ticket transitions
take a row lock and have idempotency tests but no thread racing them, and the
routing decision relies on a unique constraint rather than a demonstration.
