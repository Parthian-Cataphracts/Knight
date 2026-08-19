# Writing a KNIGHT Feature

How to build, test, publish and operate a Feature. The architecture behind it is
[`feature-delivery.md`](feature-delivery.md); this is the practical guide.

A Feature is a **versioned, deployable Django package**, never a boolean flag
([`adr/0014`](adr/0014-features-as-deployable-packages.md)). You implement it
once, publish it once, and KNIGHT delivers it to every store whose customer is
entitled to it.

---

## 1. Layout

```
features/knight-feature-<name>/
├── knight_manifest.yaml          the contract
├── pyproject.toml
└── knight_feature_<name>/
    ├── __init__.py
    ├── apps.py                   AppConfig with an explicit label
    ├── models.py                 your own tables, your own app label
    ├── migrations/
    ├── services.py               the surface other features may call
    ├── views.py / urls.py        optional
    └── checks.py                 the health check KNIGHT runs after install
```

Work from `features/knight-feature-analytics-core/` — it is a real, published,
installed Feature and the shortest way to see every part in place.

### Rules that are not negotiable

- **Own your app label, explicitly.** Set `label` on the `AppConfig`. It ends up
  in the migration table, so letting Django infer it from the module name means a
  later rename orphans every migration you have applied to every customer.
- **Never import store business code.** A Feature integrates through documented
  extension points — URL includes, signals, settings fragments, service calls
  into other Features. If uninstalling your Feature could break a store's
  checkout, the coupling is wrong.
- **Own only your own tables.** Never migrate a table another Feature or the base
  store owns.
- **No customer-specific anything.** No branding, no per-customer defaults, no
  conditionals on who is running it. One artifact goes to everybody; the moment a
  customer's value is inside it, the delivery model is broken.
- **A Feature is not a service.** No network daemon, no port. That needs its own
  ADR ([`adr/0014`](adr/0014-features-as-deployable-packages.md) §Consequences).

---

## 2. The manifest

`knight_manifest.yaml` is the contract. KNIGHT validates it at publish and the
store's installer reads it at install. Full field reference:
[`feature-delivery.md` §5](feature-delivery.md).

The two fields worth thinking hardest about:

**`migrations.reversible`** decides whether a failed upgrade can put a customer's
schema back. Declare `true` only if every operation genuinely reverses —
`CreateModel`, `AddField` with a default, `AddIndex`. A `RemoveField` or a data
migration is not reversible, and claiming otherwise means a rollback that
destroys data. When it is `false` and a migration has already applied, KNIGHT
stops with `ManualInterventionRequired` and raises an incident rather than
guessing ([`adr/0016`](adr/0016-feature-migration-and-removal-policy.md)).

**`compatibility.storeVersion`** is checked against what the store reports. Be
generous at the top end unless you actually touch store internals — a needless
upper bound creates upgrade work for every customer and every store version.

Check a manifest before you build anything:

```bash
python features/tools/knight_package.py validate features/knight-feature-analytics-core \
  --base-url http://localhost:5008 --token "$KNIGHT_TOKEN"
```

Every problem comes back at once, each with the JSON path of the field.

---

## 3. Dependencies

Declare them by slug and **version range**, never by pin:

```yaml
dependencies:
  features:
    - { slug: knight-feature-analytics-core, version: ">=1.0.0,<2.0.0" }
```

Depend on the other Feature's `services.py`, not on its models. That is what
makes a range honest: the dependency can change its storage without breaking you.

KNIGHT resolves the whole graph before it creates any job, and installs in
topological order. It refuses rather than guesses: a cycle, a range nothing
satisfies, or two Features wanting incompatible versions of a third produces no
job and an explanation. It will never downgrade an installed Feature to satisfy a
dependency.

A Feature that another installed Feature depends on cannot be uninstalled until
the dependent goes first.

---

## 4. Building and publishing

```bash
# Once, for local development only.
python features/tools/knight_package.py keygen

# Put KNIGHT_SIGNING_KEY in your environment, the public half into KNIGHT's
# FeatureArtifacts:Keys and into each store's KNIGHT_SIGNING_KEYS.

python features/tools/knight_package.py build features/knight-feature-analytics-core

KNIGHT_TOKEN=... KNIGHT_ARTIFACT_ROOT=./artifacts \
  python features/tools/knight_package.py publish features/knight-feature-analytics-core
```

`publish` builds, hashes, signs, uploads and registers the version, then
publishes it. KNIGHT re-hashes the uploaded artifact and verifies the signature
against a key it already trusts; an artifact it cannot verify never becomes
installable.

**A published version is immutable.** There is no edit. Fixing a bad release means
publishing a new version and yanking the old one — because a store that installed
`1.4.0` yesterday must get byte-identical code today, or the digest in its local
registry stops meaning anything.

### Signing keys

ECDSA P-256, DER-encoded signatures over the artifact's sha-256. Development keys
come from `keygen`; production keys live wherever custody says
([`risks.md` R21](risks.md)) and are injected as `KNIGHT_SIGNING_KEY`.

Every version records the key that signed it, so a compromised key is contained
in one action:

```
POST /api/v1/features/signing-keys/{keyId}/revoke
```

which yanks everything that key ever signed.

---

## 5. Testing

Test your Feature as a normal Django app, against a project that has it in
`INSTALLED_APPS`. Before publishing, check three things that only bite in
delivery:

1. **Migrations reverse** if you declared them reversible — actually run
   `migrate <app_label> zero` and back.
2. **The health check fails when it should.** Break the thing it depends on and
   confirm it returns `False`. A check that always passes is worse than none: it
   turns a failed install into a silent one.
3. **The store starts without your Feature.** It is optional; a store that cannot
   boot when it is absent is a Feature that has reached into the base store.

---

## 6. What happens after you publish

```
entitlement granted → resolve → job queued → agent claims it
  → preflight → fetch → verify → backup → install → migrate
  → configure → enable → reload → healthcheck → Installed
```

The store's agent polls; KNIGHT never connects inward. Each step is reported
separately and is idempotent, so a lost reply re-runs a step without re-applying
it.

If a step fails, the pipeline rolls back in reverse and reports honestly how far
it got: `RolledBack`, `PartiallyRolledBack`, or `ManualInterventionRequired`.

**Losing an entitlement disables; it never uninstalls.** The code stays, the data
stays, and a customer who renews finds everything where they left it. Uninstall is
always a deliberate, audited action, and even then the data survives for the
manifest's `dataRetentionDays`.

---

## 7. Configuration and secrets

Non-secret defaults go in the manifest. Customer-specific values live in KNIGHT
and are delivered over the install channel:

```
PUT /api/v1/installations/configuration
```

Secrets are **named** in the manifest and never valued there. Their values are
encrypted at rest in KNIGHT, travel only to the store that needs them, and are
never returned by any read API, written to a job record, or logged. Do not read a
secret into a place your Feature then logs.

---

## 8. Checklist before publishing

- [ ] `AppConfig.label` set explicitly
- [ ] No import of store business code
- [ ] `migrations.reversible` is honest, and you have run the reverse
- [ ] `compatibility` ranges reflect what you actually touch
- [ ] Dependencies are ranges against another Feature's service surface
- [ ] A health check that can fail
- [ ] Manifest validates
- [ ] Secrets named, never valued
- [ ] Version number is new — you cannot overwrite a published one
