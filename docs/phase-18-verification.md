# Phase 18 — how it was verified, and what verifying it found

Phase 18 had one exit criterion: **a store goes from empty to fully entitled and
installed through KNIGHT's own delivery path** — signed artifacts, real jobs,
real claims — with `knight_install_local` used nowhere, and a Feature upgraded
and rolled back on it afterwards.

It was chosen because every Feature in this repository had been installed with a
command that exists **precisely to bypass** the delivery engine. Phase 13 put
three Features through the real path; sixteen is a different question, and it is
the question the entire product rests on.

The phase found **eight defects**, six of which made delivery impossible. Nobody
had noticed any of them, for six phases, because the half of the system that
customers actually use had never been run.

---

## 1. What was done

| | |
|---|---|
| **Published the catalogue** | All 15 packages built, signed with a real ECDSA P-256 key, uploaded, registered and published against a running KNIGHT |
| **Onboarded a customer** | Customer, store, credentials, plan, subscription and entitlements — every step an API call an operator could make |
| **Connected the store** | Handshake, then heartbeats reporting what it runs |
| **Installed 13 Features** | By the store claiming jobs and running them: download over a signed URL, digest, signature, unpack, migrate, configure, enable, health check |
| **Upgraded and rolled back** | `reviews-ratings` 1.0.0 → 1.0.1 → 1.0.2 → back to 1.0.1, with data in the table throughout |
| **Withdrew an entitlement** | `multi-location` disabled automatically, its data kept, its routes gone |

Two Features were **correctly** not installed, and their refusals are worth
recording as evidence the resolver works rather than as gaps:

- `advanced-promotions` 2.0.0 requires a store version of `>=2.0.0` and the
  reference store reports `1.0.0`;
- `ai-reports` requires dedicated infrastructure and this store is on shared
  hosting.

`node-conformance` was refused at publish, which is also right: it is not in the
commercial catalogue, so KNIGHT has no feature identity to register a version
against. That is what "not for sale" should mean.

---

## 2. What verifying it found

### The delivery engine could not deliver anything

Four defects, each sufficient on its own to make every install fail.

**No store could report what it runs.** The compatibility resolver reads a
store's Python and Django versions from its most recent health check and treats
their absence as "cannot certify" — which is the right call. The versions were
never there. `ReadRuntimeAsync` looked for `python` and `django` at the top level
of the health document, and the documented heartbeat shape
([`store-integration.md`](store-integration.md) §5, unchanged since phase 3)
carries `dependencies` as `{database, redis, worker}` and has never had either
key. Every install of every Feature into every store was refused as
`IncompatibleStore`, permanently.

**No store could report its database either.** `StoreCompatibilityContext` has
had a `Database` field since phase 14 and the resolver has always checked it —
but `FeaturePlanContext` had nowhere to carry one from, so it was always null.
Six of the sixteen Features declare `compatibility.database`, and all six were
uninstallable for the same reason one layer up.

**A delivered package was never importable.** The installer unpacks an artifact
into `<feature_root>/<slug>/` so the previous version can sit beside it — and
only `<feature_root>` was on `sys.path`, so the package one directory deeper
could not be imported at all. `manage.py migrate` answered *"No installed app
with label 'knight_analytics_core'"* about a package sitting on disk two
directories away.

**A Feature was registered too late to be migrated.** The registry entry was
written by `enable`, which runs *after* `migrate` — so the migrate subprocess,
whose entire reason for being a subprocess is "a fresh app registry that includes
the feature just installed", was built from a registry that did not include it.

### Rollback was worse than broken

Three defects, and together they made rollback dangerous rather than merely
useless.

**The backup was deleted before anything could restore it.** `backup` kept the
outgoing package tree in the job's workspace, and the runner deletes a workspace
when its job finishes. A rollback is a *different* job, so `restore-package`
found nothing, said "no previous version to restore", and **the job reported
success**.

**Every rollback dropped the Feature's tables.** `reverse-migrate` computed its
target as `context.previous.version if context.previous else "zero"`, and
`RollbackSteps` has no `preflight` step — which is where `previous` is populated.
So it was always `None`, the target was always `"zero"`, and every rollback
migrated the app to zero and destroyed the merchant's data. It then reported
success. (The non-null branch could never have worked either: it passed a
*release version* where `manage.py migrate` wants a *migration name*.)

**KNIGHT could never record a rollback.** The job was queued beside an
installation that knew nothing about it, so the store's completion report was
refused — *"an installation in state 'Installed' cannot be marked installed"* —
the job stayed `Running` for ever, and the control plane went on reporting the
version the store had just rolled away from.

### And two in what an operator is told

**A Feature that could not be installed was reported as one that does not
exist.** A failed plan carries no steps, so there is no root step to take a
feature id from, and the code read a null id as a missing Feature. Fifteen
refusals came back as *"No feature is registered with slug 'analytics-core'"* —
about Features the API had listed two calls earlier. The real reasons were in the
plan the same request had just produced and were discarded, along with the
blocking reason that [`feature-delivery.md`](feature-delivery.md) §8 says the
installation row records.

**Then the fix for it answered 500.** Making the service correct left the
response mapper dereferencing an installation that is now legitimately null, so
the careful refusal reached the operator as *"an unexpected error occurred"* —
worse than the wrong 404 it replaced. Four tests written with the first fix all
called the service directly and all passed. Only a request through HTTP found it,
which is the shape those tests should have had.

### An upgrade that upgraded nothing

The resolver prefers an installed version when it still satisfies the
constraints — correct for a dependency, and the reason is good: *an install of
one Feature should not quietly bump three others*. It applied to the root as
well, so `upgrade` with no version named resolved to the version already
installed and queued no job. A fleet could only be moved forward by an operator
who already knew the version number. Roots now move forward on an upgrade;
dependencies still do not.

---

## 3. Why none of this was caught before

Every one of these lives in the path a customer travels and not in the path a
developer does. A Feature is authored with `knight_install_local` and a
`pip install`, which puts the package in site-packages and writes the registry
directly — no plan, no compatibility check, no artifact, no job, no migrate
subprocess, no rollback.

That command exists for a good reason and the reason is written in its own
docstring. What phase 17 had already shown, and phase 18 confirmed at scale, is
that **two paths that are never run against each other drift**: the runtime check
lived in `preflight` and had to be added to `knight_install_local` separately,
because they had silently become different code.

The lesson worth keeping is not "test more". It is that the delivery path needs a
run like this one on a schedule, because its failures are invisible to every
other kind of testing this project does.

---

## 4. How to test it

The whole run is scripted in the phase's scratch directory, but the steps are
these, and each is worth doing by hand once.

### Publish

```bash
python features/tools/knight_package.py keygen
```

Put the private half in `KNIGHT_SIGNING_KEY` and the public half in
`FeatureArtifacts__Keys__dev__PublicKey`, then start the API with
`FeatureArtifacts__ArtifactRoot` pointing somewhere writable.

```bash
python features/tools/knight_package.py publish features/knight-feature-analytics-core --dist /tmp/dist --artifact-root /tmp/artifacts --base-url http://localhost:5008 --token "$TOKEN"
```

Expect `Registered … / Published …`. A Feature with no catalogue identity is
refused here, which is correct.

### Onboard, and connect the store

Create the customer, store, credential and subscription through
`/api/v1/customers`, `/api/v1/stores`, `/api/v1/stores/{id}/credentials` and
`/api/v1/subscriptions`. Two refusals along the way are the API being right:
selecting a Feature the plan *includes* rather than offers, and selecting one
that needs dedicated infrastructure for a shared-hosted store.

Put the credential in the store's environment and:

```bash
python manage.py knight_register
```

```bash
python manage.py knight_heartbeat
```

**Then check what KNIGHT learned**, because this is the step everything else
depends on:

```bash
docker exec docker-postgres-1 psql -U knight -d knight -c 'select "Dependencies"::text from control.store_health_checks where "Source" = '"'"'Heartbeat'"'"' order by "CheckedAt" desc limit 1;'
```

It must contain `"runtime": {"database": "postgresql", "django": "…", "python":
"…"}`. Without that block nothing can be installed, and the refusal an operator
sees will be about compatibility rather than about the missing report.

### Install

```bash
curl -X POST http://localhost:5008/api/v1/installations/plan -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"storeId":"…","slug":"analytics-core","versionRange":null}'
```

A plan with `"isSuccessful": true` and one `Install` step. Then the same body to
`/installations/install`, and on the store:

```bash
python manage.py knight_apply_job --max-jobs 30
```

Repeat both until nothing moves: a dependency has to be installed before the
thing that needs it can be planned, and `'analytics-core' already has work in
flight` is the engine refusing to run two jobs at once, not an error.

**Restart the store after installing**, with `--noreload`. The feature registry
is read once at start-up and is not a file the autoreloader watches, so a store
started before a Feature was registered serves 404 for it and looks like a
mounting bug.

### Upgrade and roll back

Bump a manifest's version, publish it, then:

```bash
curl -X POST http://localhost:5008/api/v1/installations/upgrade -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"storeId":"…","slug":"reviews-ratings","versionRange":null}'
```

`versionRange: null` must plan `1.0.1 -> 1.0.2 Upgrade`. If it says
`AlreadySatisfied`, the root-preference defect is back.

Put a row in the Feature's own table first, then roll back:

```bash
curl -X POST http://localhost:5008/api/v1/installations/rollback -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"storeId":"…","featureId":"…","reason":"drill"}'
```

Three things must all be true afterwards, and each corresponds to one of the
rollback defects: the store runs the earlier version, **the row is still there**,
and KNIGHT records `InstalledVersion` at the earlier version with state
`Installed` rather than a job stuck `Running`.

### Withdraw an entitlement

```bash
curl -X POST "http://localhost:5008/api/v1/customers/$CUSTOMER/entitlements/$FEATURE/revoke" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"reason":"drill"}'
```

A `Disable` job appears without anybody queueing one. After the store runs it and
restarts: the Feature's routes are **404**, every other Feature still answers
200, and the Feature's data is untouched.

---

## 5. Test results

| Suite | Result |
|---|---|
| Backend unit | **645 passed** (640 before) |
| Backend architecture | 13 passed |
| Backend integration, `REQUIRE_POSTGRES_TESTS=1` | **160 passed** (155 before) |
| Store, all Features installed, `REQUIRE_FEATURE_TESTS=1` | 775 passed, 0 skipped |
| Node reference store | 14 passed |
| Dashboard | 9 passed, `tsc --noEmit` clean |

The new tests are the ones that would have caught what this phase found by
hand: the resolver's root-versus-dependency rule, the installation aggregate's
rollback round trip, and the blocked-install path through both the service and
the HTTP endpoint.

---

## 6. What is deliberately not covered

**The eight defects have tests; the run itself does not.** What would catch a
ninth is this run, automated — publish, onboard, install, upgrade, roll back,
withdraw — against a real API and a real store. It is the obvious next piece of
work and it is in [`../TODO.md`](../TODO.md); it did not happen inside this phase
because finding and fixing eight defects filled it.

**The domain-verification path was not exercised.** The store stayed `Pending`
throughout, because proving `camden.coffee` needs a domain that resolves. Nothing
in the delivery path gated on it, which is itself worth knowing: a store can be
installed into before its domain is proven.

**KNIGHT's own health poller does not capture the runtime block.** The heartbeat
does, and that is what delivery reads. A store that KNIGHT polls but which never
heartbeats would still be uncertifiable — it is a narrow gap and it is written
down rather than fixed.

**The rollback drill used a synthetic version.** `reviews-ratings` 1.0.1 and
1.0.2 differ only in their manifest version, so the reverse migration had nothing
to undo. That is enough to prove the tables survive and the versions move, and
not enough to prove a real down-migration works. A Feature with two genuinely
different schemas is what would prove that.
