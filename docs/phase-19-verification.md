# Phase 19 — how it was verified, and what verifying it found

Phase 19 had one exit criterion: **the journey phase 18 walked by hand runs as
one command, in CI, on every push**, and fails loudly when any step of it
regresses.

Phase 18 nominated this phase in its own write-up, and the reason it gave still
holds. Eight defects lived in the delivery path for six phases. Every one of them
now has a test, and not one of those tests would find the *next* one: they check
the code each defect happened to be in, and what the eight had in common was the
path.

The drill found a ninth on its first complete run, and two more once it ran
somewhere that was not a developer's machine.

---

## 1. What was built

[`tools/delivery-drill/drill.py`](../tools/delivery-drill/drill.py) — publish,
onboard, connect, install, upgrade, roll back, withdraw, against a real API, a
real database and a real store, asserting at every step and exiting non-zero on
the first thing that is not true. [Its README](../tools/delivery-drill/README.md)
says what each step proves.

It carries **its own Feature in two versions**. 1.1.0 adds a `note` column that
1.0.0 does not have, so the upgrade has a migration to apply and the rollback has
one to reverse. Two source trees rather than one with a switch, so the whole
difference is a diff. The Feature's catalogue identity is created through the API
at run time and never seeded, so the sellable catalogue stays free of test
fixtures — the separation `node-conformance` already keeps for the node runtime.

Real catalogue Features are installed alongside it, so a run proves the actual
packages still build, sign, publish and install rather than only that the
machinery moves.

[`.github/workflows/delivery.yml`](../.github/workflows/delivery.yml) runs it on
every push and every pull request: Postgres, a generated signing pair, the
control plane bootstrapped, the API started, the drill.

---

## 2. What verifying it found

### A rollback rolled the code back and left the database where it was

`RollbackSteps` ran `restore-package` before `reverse-migrate`. Restoring the
package puts the previous version's tree back — **which removes the newer
version's migration file** — and Django can only unapply a migration whose file
it can still see. The reverse that followed found nothing to undo and succeeded
by doing nothing.

The result is a store running 1.0.0's code against 1.1.0's schema, reported as a
clean rollback. That is worse than a failure: the next install, restore or
`makemigrations` sees a database that does not match anything, and nothing in the
system says so.

The fix is an ordering — reverse first, then restore — plus reading the reverse
target from the kept backup rather than from the target tree, since by then the
target tree is the one being replaced.

**Phase 18 could not have found this.** Its rollback moved between
`reviews-ratings` 1.0.1 and 1.0.2, whose migrations were identical, so the
reverse migration had nothing to undo either — and a reverse that correctly does
nothing looks exactly like one that wrongly does nothing. Phase 18 wrote that
limitation down in its own §6. This is what writing it down was for.

The ordering is now pinned by a test, because nothing else in the suite would
notice it moving: every rollback test passes with the steps in either order.

### A manifest this repository ships could not be published from a clean checkout

The packaging tool prefers PyYAML and falls back to a small reader for the
manifest subset when it is absent — which is every clean checkout, since nothing
here depends on PyYAML. That reader had no branch for an inline list, so
`extensions: []` came back as the *string* `"[]"` and `extensions: [pg_trgm]` as
`"[pg_trgm]"`.

Publishing then failed with `'extensions' must be an array of strings` — a
refusal from KNIGHT's own validator, about a manifest that is perfectly valid.
Every developer who had PyYAML installed saw it work.

Two things about this are worth keeping. The first is that **it was caught in the
right place**: the reader's own docstring says a manifest misread here becomes a
signed artifact with the wrong contract, and what actually happened is that the
control plane refused to register it. The system was right twice.

The second is why nothing found it earlier. The reader has a differential
selftest — parse every manifest with both readers, require the same document — and
it runs in CI. It globbed `features/knight-feature-*/`, and the drill's Feature
is deliberately not in the catalogue and does not live there. It was also the
only manifest in the repository using an inline list. The selftest now covers it.

### The workflow signed under a key id KNIGHT was not configured to trust

The first CI runs generated a pair, registered its public half under the id `ci`,
and left the packaging tool signing under `dev`, which is its default. Every
publish was refused with *"the signature over the artifact digest is not valid
for signing key 'dev'"* — true, and about the signature rather than about the
name, which is the wrong half of the problem to be told about.

The drill now builds the store's trusted key map from `KNIGHT_SIGNING_KEY_ID`
rather than from the literal `dev`, so the id the artifact is signed under and
the id the store trusts cannot drift apart again.

### Two things the drill found about itself, worth keeping

**It reused one store database and the second run failed.** The table still had
1.1.0's column while the package on disk was 1.0.0's, so an insert hit a `NOT
NULL` violation. The drill now creates its own database per run. A drill telling
the truth about its own environment rather than about the product is the least
useful kind of red, and the one most likely to be ignored.

**It needed a second factor it could not have.** CI creates a fresh SuperAdmin on
every run, and a fresh SuperAdmin holds no permissions until it enrols one. The
alternative was carrying a TOTP secret as a repository secret — a shared
credential in a workflow whose entire point is that it starts from nothing. The
drill now enrols its own, walking the same three requests a person walks on first
sign-in.

### And one in another suite, found on the way

The node store's suite ran `node --test test/`. A directory argument worked on
node 20, which CI pins, and stopped working on node 24, which a current developer
machine has — so the suite was green in CI and could not be run locally at all.
It now uses a glob and CI runs node 22, which is the form both agree on.

---

## 3. How to test it

### In CI

Push. The **Delivery / Delivery drill** job is the whole thing. It needs no
secrets: it generates its own signing pair and its own administrator password.

### By hand

Postgres on 5433, then:

```bash
dotnet run --project backend/tools/Knight.Bootstrap -- --email drill@knight.test
```

```bash
python features/tools/knight_package.py keygen
```

Start the API with the public half in `FeatureArtifacts__Keys__<id>__PublicKey`,
`FeatureArtifacts__ActiveKeyId` naming that id, and
`FeatureArtifacts__ArtifactRoot` somewhere writable. Then:

```bash
python tools/delivery-drill/drill.py
```

with `KNIGHT_ADMIN_EMAIL`, `KNIGHT_ADMIN_PASSWORD`, `KNIGHT_SIGNING_KEY` (the
private half), `KNIGHT_PUBLIC_KEY` (so the store can verify) and
`KNIGHT_ARTIFACT_ROOT` set. Give `KNIGHT_ADMIN_TOTP` only for an account that
already has a second factor; without it the drill enrols one.

Expect nine numbered steps and:

```
The delivery drill passed: every step of the journey is still true.
```

A failure names the step and the assertion. The one that matters most is in step
08 — *the rollback reversed 1.1.0's migration rather than leaving the schema where
it was* — because that is the assertion the ninth defect failed, and the whole
reason the drill carries two schemas rather than two version numbers.

### To watch it fail

Swap `ReverseMigrate` and `RestorePackage` back in
[`FeatureInstallationJob.cs`](../backend/modules/FeatureDelivery/Domain/FeatureInstallationJob.cs).
The unit test goes red immediately and the drill goes red on step 08. Both should,
and before this phase neither did.

---

## 4. Test results

| Suite | Result |
|---|---|
| Backend unit | **646 passed** (645 before) |
| Backend architecture | 13 passed |
| Backend integration, `REQUIRE_POSTGRES_TESTS=1` | 160 passed |
| Store, all Features installed, `REQUIRE_FEATURE_TESTS=1` | 775 passed, 0 skipped |
| Node reference store | 14 passed |
| Dashboard | 9 passed, `tsc --noEmit` clean |
| **Delivery drill** | **passed** — 9 steps, 24 assertions, against a running API and store |
| **Delivery drill, in CI** | **passed** — the same 24, on a runner from an empty database |

The drill was run three times against a live stack: once with an administrator
that already had a second factor, once against a freshly bootstrapped one with
none, and once against an entirely empty control plane with a newly generated
signing pair — which is the shape CI runs, reproduced locally after CI failed
twice with logs that were not readable from here. Then in CI itself, green.

---

## 5. What is deliberately not covered

**The drill installs the catalogue but does not exercise each Feature.** It
proves the packages install; what they then do is the store's own suite, and
duplicating it here would make a slow job slower without making it say more.

**Only the django runtime.** The node store has its own delivery suite and it is
a real one, but nothing yet drives a node store through KNIGHT's job path the way
this drives the Django one. That is the obvious next piece and it is written down
rather than done.

**Nothing fails on purpose.** Every assertion checks a success. A drill that also
proved the engine *refuses* correctly — an artifact whose signature is wrong, a
store that reports an incompatible runtime — would be worth having, and the
refusal paths currently rest on unit tests alone.
