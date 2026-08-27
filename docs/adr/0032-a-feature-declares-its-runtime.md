# 0032 — A Feature declares its runtime, and the wiring is named the same way for all of them

- Status: **Accepted**
- Date: 2026-08-27
- Amends: [`0014`](0014-features-as-deployable-packages.md)
- Closes: R26 and decision 14 in [`../risks.md`](../risks.md)

## Context

Everything about Feature delivery is runtime-neutral except the one file that
decides whether a Feature may be published at all.

The ingestion contract is plain HTTP. The job vocabulary is a closed list of
names — `Install`, `Upgrade`, `ApplyConfiguration`, `Enable`, `Disable`,
`Uninstall`, `Rollback` — carried out as `preflight`, `fetch`, `verify`,
`backup`, `install`, `migrate`, `configure`, `enable`, `disable`, `reload`,
`healthcheck`, and a store performs each of those however its own runtime does.
Artifacts are signed zips. None of that knows what a Django is.

`ManifestReader` does. It refuses a manifest with no `django:` block and
validates `app_label` and `installed_app` as Python identifiers, so a Feature
cannot be *published* for a store that is not a Django application, never mind
installed into one.

That left the project saying two things that do not fit together. A Feature is
versioned, deployable code and never a flag
([`0014`](0014-features-as-deployable-packages.md)) — and a non-Django store has
nothing available to it but a flag it enforces itself. The gap was invisible
until somebody tried, which is the worst property a gap can have.

It was recorded as R26 and left to the product owner, who has now answered:
**yes** — a Feature must be publishable for a store that is not Django.

## Decision

### 1. The manifest declares its runtime

```yaml
runtime: django
```

A closed list, exactly like `schedule` and `strategy`, and for the same reason: a
free string is a parser, a support surface and eventually a runtime nobody has
ever tested. Adding a name to the list is a deliberate act with a store behind
it.

**Omitted means `django`.** Thirteen manifests were written before this field
existed and every one of them is a Django Feature; refusing them would be
breaking a published contract to add a field whose value they all imply. They
have each been given the line explicitly anyway, because a contract that is
stated is worth more than one that is inferred — but the default is what makes
that a tidy-up rather than a migration.

### 2. The runtime block is named by the runtime

```yaml
runtime: django
django:
  app_label: knight_subscriptions
  installed_app: knight_feature_subscriptions
  urls: { include: knight_feature_subscriptions.urls, prefix: subscriptions/ }
```

```yaml
runtime: node
node:
  namespace: knight_subscriptions
  module: "@knight/feature-subscriptions"
  mount: { export: router, prefix: subscriptions/ }
```

One block, named for the runtime that reads it, so a manifest never carries
wiring for a runtime it does not use and a reader never has to decide which of
several blocks is authoritative.

### 3. Three names are runtime-neutral; only their spelling is not

This is the part worth keeping. Every runtime that can receive a Feature needs
the store to be told exactly three things, and Django's four fields are those
three wearing Python clothes:

| Neutral name | What it is | Django | Node |
|---|---|---|---|
| **namespace** | what this Feature's migrations and state are recorded under | `app_label` | `namespace` |
| **module** | what the store loads to get the code | `installed_app` | `module` |
| **mount** | the exported symbol serving routes, and the path it mounts at | `urls.include` + `urls.prefix` | `mount.export` + `mount.prefix` |

So the parsed form, the wire contract and the store's installer all speak in
those three names, and only the *reader* knows how a given runtime spells them —
along with what makes each spelling valid, which is genuinely per-runtime:
`app_label` must be a Python identifier because it ends up in a Django migration
table, and `module` must be a valid npm specifier because it ends up in an
`import`.

The alternative — carrying an untyped bag of per-runtime keys all the way to the
store — was rejected. It would move every validation from publish time to install
time, which is the trade this whole delivery model exists to refuse: a manifest
that is wrong should fail in a pipeline, not halfway through somebody's database.

### 4. A runtime is not real until a store has received a Feature over it

A name in the list with nothing behind it is a promise, and this repository does
not ship promises as features. `node` is in the list because
[`stores/node-reference-store`](../../stores/node-reference-store) exists and
takes delivery: it verifies a real signature over a real artifact, unpacks it,
records the migration under the declared namespace, writes the configuration
where the Feature looks for it, mounts the route the manifest declared, and
answers the health check. `features/knight-feature-node-conformance` is what it
receives, and CI runs the whole thing on every push.

One boundary, stated rather than glossed: that store reads its job payload from a
file instead of exchanging a token and claiming work over HTTP. The transport is
identical to the Django store's and duplicating it would have demonstrated
nothing about runtime neutrality, which is the only thing that store exists to
demonstrate. Everything downstream of the payload arriving is real.

Adding a third runtime means the same bar: a reader case, a spelling table row,
and a store that has actually taken delivery.

### 5. The wire keeps `django` for one release

The job payload gains a neutral `runtime` object and **keeps sending `django`**
where the runtime is django. A store is upgraded on its own schedule and a
staged rollout deliberately leaves some stores behind; a payload that dropped
the old key would break the ones that had not caught up, at the exact moment
they were being asked to install something.

The reference store prefers `runtime` and falls back to `django`. The `django`
key is deprecated on the wire from this release and comes out when no supported
store reads it.

## Consequences

**A Feature may now be published for a non-Django store**, which is what R26
asked for and what [`0014`](0014-features-as-deployable-packages.md) implied all
along.

**The delivery path got wider, and that is the highest-risk surface in the
system** (R16). Nothing about signing, verification or job authorisation
changed — a node package is fetched, verified against the same ECDSA P-256
signature and installed under the same job vocabulary — but there is now a second
installer path, and the external review scoped in
[`../security/external-review-scope.md`](../security/external-review-scope.md)
has a second store runtime in it.

**No database migration.** The runtime wiring was already read out of the stored
manifest JSON at delivery time rather than duplicated into columns on
`FeatureVersion`, so this changed the parsed shape and not the schema. That was
luck the original author earned.

**A manifest with `runtime: node` and a `django:` block is refused**, as is the
reverse. A Feature declaring wiring for a runtime it is not built for is an
author who has copied a file, and it is cheaper to say so at publish.

**Packaging is per runtime.** `knight_package.py` lays out a Django package as
the Python distribution it already built, and a node package as its `package.json`
and built output. The archive, the digest and the signature are unchanged, which
is why the delivery path did not have to learn anything new.
