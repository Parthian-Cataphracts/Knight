# Phase 22 — the API-driven architecture, and how it was verified

Phase 22 added a second way for a Feature to reach a store: as a service the
store talks to, rather than as code the store runs.

It was not a rewrite and it did not remove anything. `architecture:` joins
`runtime:` as a discriminator, sixteen existing Features are untouched, and every
suite that passed before this phase passes after it. The direction is documented
in [`adr/0033`](adr/0033-api-driven-features.md); the deprecation of in-process
delivery is a direction rather than an enforcement, because a delivery mechanism
cannot be removed faster than its slowest customer can migrate.

---

## 1. Why

Building the third store agent is the evidence. It cost a library, fifteen step
verbs and nineteen tests — and along the way the node agent turned out to have
been missing three verbs for three phases and three more for four, so it could
not roll back or uninstall anything and nobody had noticed.

150 Features across three runtimes is 450 packages to build, sign, version,
install, migrate and roll back, each running inside a store we do not operate,
holding that store's database handle. Six of the eight defects phase 18 found
were in exactly that machinery.

---

## 2. What was built

| | |
|---|---|
| **Schema** | `architecture: external_service`, with `service`, `webhooks`, `api_proxies` and `ui_mounts`. `runtime`, `migrations`, `install`, `dependencies` and `workers` are refused on one |
| **Packaging** | No archive. The signed **configuration document** is the artifact: same digest, same signature, same `fetch` then `verify` |
| **Pipelines** | Six external step lists, built entirely from verbs the three agents already implement |
| **Three agents** | Django, node and .NET each register rather than unpack, and each validates the events and slots against its own catalogue |
| **Proof of concept** | `subscriptions` 2.0.0 — the same Feature as 1.x, run as a service |
| **Drill** | An eleventh step that walks the API-driven path end to end |

The Django store got real machinery rather than a stub: an event catalogue, a bus
that reads the registry on every publish, HMAC request signing over method, path,
timestamp, nonce and a digest of the body, and a proxy that strips every
credential the shopper carries.

---

## 3. What verifying it found

### The architecture never reached the wire

The first real drill run failed at `preflight.wrong_runtime`: *"This store runs
django and the job delivers a external package."*

`architecture` had been added to the manifest, the plan step, the job aggregate,
the installation row and the deliverable version — and not to `AgentJobAssignment`
or `AgentJobResponse`. So the store received a job with no architecture on it,
defaulted to in-process, looked for a runtime block, found `external`, and
refused the whole thing.

Every unit test passed. The end-to-end run caught it on the first attempt, which
is precisely what phase 19 built the drill for.

### The external rollback had nothing to restore from

Caught while writing it rather than by a test, and worth recording because the
first design was wrong in a way that reads as correct.

The external rollback pipeline was `[restore-package, configure, install, enable,
healthcheck]` with no `backup` in the install pipeline — on the reasoning that an
external Feature has no package to back up. It has nothing to back *up*, and it
has something to back up: **the answer to "what was registered before this"**.

Without it, a rollback would have had to fetch the older version's configuration
— and a rollback job names the version it is rolling *to* while carrying the
artifact of the one it is rolling *from*, so the store would have reinstalled the
version it was trying to leave. `backup` keeps the registration beside the
feature root, and `restore-package` puts it back.

### The node store still could not roll back or uninstall anything

Found while adding the external branches. Its step table had no
`restore-package`, no `remove-package` and no `reverse-migrate`, and carried an
`uninstall` step KNIGHT has never sent.

So every rollback and every uninstall on a node store was refused as
`step.unknown`. The same shape of gap phase 20 found for `backup`,
`create-extensions` and `reload`, in the same store, four phases later — because
nothing had ever sent it one of those jobs.

### Two agents created a directory for code that does not exist

Both the Django and the node `configure` step wrote the Feature's configuration
into `<feature_root>/<slug>/`, creating the directory if it was not there. For an
external Feature that leaves a store with an empty directory named after a
Feature that has no package, which the next person to look at it would reasonably
read as a half-finished install. Both now write beside the feature root instead.

---

## 4. How to test it

### The manifest

```bash
python features/tools/knight_package.py selftest
```

Nineteen manifests, including the proof of concept. Then build it:

```bash
python features/tools/knight_package.py build features/knight-feature-subscriptions-service --dist /tmp/dist
```

Expect `configuration only - no code` and a **`.json`** file. A `.zip` here means
the architecture was not read.

### The whole journey

```bash
python tools/delivery-drill/drill.py
```

Step 11 is the one this phase added. It asserts three absences that are the whole
point, and each of them is a thing that would have been true of the in-process
path:

- **no table** was created in the store's database, before or after the install,
  and none was dropped when the entitlement was withdrawn;
- **no package directory** exists under the feature root;
- **no `migrate` step** was ever run for the Feature.

Plus the presences: four webhooks registered, two proxy routes, two UI mounts,
and the store's own registry recording it as a service rather than a package.

### By hand, if you want to see the refusals

Publish an external manifest that subscribes to an event the store does not
publish, and watch the install fail at `install.unknown_event` with the store's
own catalogue in the message. KNIGHT accepted it — the name is well-formed — and
the store refused it, which is the division of labour this architecture depends
on.

---

## 5. Test results

| Suite | Result |
|---|---|
| Backend unit | **680 passed** (655 before) |
| Backend architecture | 13 passed |
| Backend integration, `REQUIRE_POSTGRES_TESTS=1` | 164 passed |
| Django store, all Features, `REQUIRE_FEATURE_TESTS=1` | **801 passed** (775 before) |
| Node reference store | **30 passed** (18 before) |
| .NET store agent | **31 passed** (19 before) |
| Delivery drill | passed, 12 steps |

Run at the two gates the brief asked for — after the agents and after the drill —
and both times all four were green before anything was committed.

---

## 6. What is deliberately not covered

**Nothing runs at the other end.** `subscriptions` 2.0.0 names
`https://subscriptions.knight.dev` and no such service exists. The delivery path
is proven end to end; the service behind it is a deployment, and standing one up
is a separate piece of work. Every assertion in the drill is about what the store
did with the configuration, and none of them requires the service to answer.

**No event has actually been delivered.** The Django bus resolves subscribers,
respects a lapsed entitlement and fans out without letting one subscriber's
failure stop another — and the transport itself is left to the store, because
whether that is Celery, a database-backed queue or a thread is a decision about
the store's reliability rather than about the Feature. The reference default logs
and says so rather than pretending.

**No request has actually been proxied.** The proxy strips credentials, enforces
identity and method, signs the assertion and maps a dead upstream to 502, and
every one of those is unit-tested — against a mocked transport, because there is
nothing to call.

**The retry policy for `at-least-once` is named, not built.** The manifest lets a
Feature choose the guarantee and the store records it; what happens on the third
failed delivery is the store's, and this reference store does not have a queue to
have a policy about.

**A store cannot pin a service version.** This is the real cost of the trade and
it is worth stating plainly: versioning the configuration does not version the
service behind it. An author can now change behaviour for every store at once,
which is the same property that lets them fix a bug for every store at once.
