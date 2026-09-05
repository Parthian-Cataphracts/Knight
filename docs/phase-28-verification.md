# Phase 28 — how far it got, and what the table decided

Phase 28's exit criterion: **every one of the sixteen Features has a recorded
decision — service, or in-process, with the reason — and the ones that should
move have moved.** Its gate: **the decision table exists for all sixteen, and
the ones marked "service" are delivered as services and driven by the drill.**

The phase's own framing is that the honest split *is* the deliverable, and that
half is done: [`feature-architecture-decisions.md`](feature-architecture-decisions.md)
records all sixteen with a reason each. The moves are not, and the reason is
scale rather than doubt — `subscriptions` took two phases and found six defects
on the way, every one invisible until two processes had to agree.

---

## 1. What was decided

**Four should be services; one of them is.**

| | |
|---|---|
| `subscriptions` | already a service, since phase 22 |
| `external-marketplaces`, `marketing-automation`, `ai-reports` | should be, and have not moved |
| Eight Features | stay in-process, and eight of those are the transaction argument |
| Three | genuinely arguable, held together by their dependency on `analytics-core` |
| `log-shipping` | neither: the work is KNIGHT's own, and there is nothing to install |

The test that decides most of it is one question — *does this Feature have to be
inside the store's own database transaction?* There is no way to be inside a
transaction over HTTP, so `advanced-inventory` reserving stock during checkout,
`gift-cards` spending a balance, `advanced-promotions` pricing an order and
`loyalty-rewards` redeeming points are settled rather than arguable. The table
also records what *would* change each decision, so the next argument starts from
evidence instead of from scratch.

## 2. What was built

- **The decision table**, for all sixteen, with the reason against each and the
  three conditions that would revise it.
- **Configuration is validated against the manifest.** Carried from phase 3.5.
  A setting the manifest never declared, or one of the wrong type, or a secret
  the Feature will never read, is refused with the key named — rather than
  saved, encrypted, and silently doing nothing. Judged against the manifest of
  the version the store actually has, because that is the document the author
  signed.

The shared secret an `external_service` Feature names under `service:` counts as
declared, which is not a detail: KNIGHT issues that one itself, and leaving it
out would have made phase 24's own delivery path fail its own validation.

## 3. What verifying it found

**`cart.abandoned` cannot be published, and the reason is not a missing
publisher.** The item was carried as "the store's event catalogue now exists and
`cart.abandoned` is in it, so this is a publisher rather than a design". It is a
design: **the reference store has no cart model at all**. There is nothing that
holds a cart long enough to be abandoned, so an event saying one was would be
fabricated. `marketing-automation` needs the base store to grow a persisted
cart first, and that is base-store product work rather than a line in a
publisher.

**A manifest that cannot be read must judge nothing.** The first version of the
validation treated an unparseable manifest as "declares nothing", which refuses
every setting an operator tries to save — punishing them for a fault that is not
theirs. It reports *unknown* instead, and unknown means do not judge.

## 4. How to test it

```bash
cd backend
REQUIRE_POSTGRES_TESTS=1 dotnet test --filter FullyQualifiedName~ConfigurationContractTests
```

Six tests: a declared setting is accepted, a typo is refused with the correct
spelling in the message, a string where a number belongs is refused, `null`
clears a setting rather than failing type-checking, an undeclared secret is
refused, and a declared one is accepted.

By hand, against a store with a Feature installed:

```
PUT /api/v1/installations/configuration
{ "storeId": "…", "featureId": "…", "values": "{\"retry_attemps\": 5}" }

400 — values.retry_attemps: This Feature declares no setting called
     'retry_attemps'. It declares: digest, retry_attempts, sender_name.
```

## 5. The numbers

| | |
|---|---|
| KNIGHT backend | **691 unit**, **13 architecture**, **174 integration** (six new) |

## 6. What is still not done

- **The three Features marked "service" have not moved.** Each is a
  phase-22-sized piece: a service with its own database and deployment, a
  manifest rewritten as `external_service`, a store command for anything the
  service may not do itself, and a drill step — because "installed" and
  "working" are different claims and only the drill tells them apart. Moving
  three in one pass without that scrutiny each would be the same mistake at
  three times the size.
- **The gate is therefore half met.** The table exists for all sixteen; the ones
  marked "service" are not yet delivered as services or driven by the drill.
- **A vendor wired to one of them** still needs four accounts, which is a
  commercial decision. Four Features refuse honestly without a credential and
  none has ever called anybody.
- **`cart.abandoned`** needs a cart in the base store — see above.
- **The orphan identities** (`analytics`, `loyalty`, `order-management`,
  `ai-recommendations`) are now retired by the seeder itself: they are named in
  the catalogue data under `retired`, and each is withdrawn where a past
  deployment seeded it. A withdrawal is a status change, never a delete — a
  customer who somehow still held one keeps their record — and it is a no-op on a
  deployment that never had them, which is why it belongs in the additive seeder
  rather than in an operator's memory. An integration test stands an orphan up
  and reseeds to prove it is withdrawn.
- **Per-feature plan composition with time-boxed prices** is done: the backend
  already composed plans and time-boxed prices (a price change closes the one it
  replaces at the same instant the new one opens, per plan); the operator UI now
  sets each feature's membership on a plan and its price — general or plan-scoped
  — and shows the price history. **Feature and version creation from the
  dashboard** remains (carried from phase 6).
