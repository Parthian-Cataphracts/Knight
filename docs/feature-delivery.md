# Feature Delivery

Status: **authoritative**. This document is the centre of the corrected
architecture. Decisions: [`adr/0014`](adr/0014-features-as-deployable-packages.md),
[`adr/0015`](adr/0015-feature-delivery-mechanism.md),
[`adr/0016`](adr/0016-feature-migration-and-removal-policy.md),
[`adr/0017`](adr/0017-feature-compatibility-and-dependencies.md).

## 1. The correction

A KNIGHT Feature is **not** a boolean flag. It is a **versioned, reusable,
deployable Django artifact**.

```
WRONG                                RIGHT
feature_enabled = true               analytics-core == 1.4.0
                                     installed, migrated, configured, healthy
```

A Feature is implemented **once**, packaged, registered in KNIGHT, and then
delivered automatically to every store whose customer is entitled to it. A
developer never re-implements or hand-copies a Feature into a store.

```
IMPLEMENT ONCE → PACKAGE → REGISTER → CUSTOMER PURCHASES → ENTITLEMENT
   → INSTALLATION JOB → DELIVER → INSTALL → MIGRATE → CONFIGURE → ENABLE
   → HEALTH CHECK → MONITOR → UPGRADE / ROLLBACK
```

## 2. Two orthogonal concepts

```
ENTITLEMENT     the customer is allowed to use Feature X   (commercial fact)
INSTALLATION    Feature X is deployed and healthy in this store   (technical fact)
```

They are separate rows, separate lifecycles, separate failure modes, and they
are **never** collapsed into one boolean. Valid combinations include:

| Entitlement | Installation | Meaning |
|---|---|---|
| true | `NotInstalled` | Purchased, delivery not started |
| true | `Installing` | Delivery in progress |
| true | `Failed` | Paid for, not working — must be visible and alertable |
| true | `Installed` | Normal operating state |
| false | `Installed` (`Disabled`) | Subscription ended; code present, feature off, data retained |
| false | `NotInstalled` | Fully removed |

Billing follows entitlement. Runtime behaviour follows installation **and**
entitlement: a store enforces both — installed code still refuses to run
without a valid entitlement.

## 3. Feature Registry

KNIGHT owns the central catalogue of deployable Features.

```
Feature                 identity of a capability across all versions
  slug                  analytics-core
  name, description, category
  status                Draft | Published | Deprecated | Withdrawn
  isOptional            can a customer add it to a plan?
  requiresDedicatedInfrastructure

FeatureVersion          one immutable, deployable release
  featureId, version (semver)
  packageReference      registry coordinates (name + version)
  artifactDigest        sha256 of the built artifact
  signature             detached signature over the digest
  manifest              parsed manifest (jsonb)
  status                Draft | Published | Yanked
  releaseNotes, publishedAt, publishedBy
```

A `FeatureVersion` is immutable once published. Fixing a bad release means
publishing a new version and yanking the old one — never mutating it.

## 4. Feature package

A Feature is a normal installable Django package — an app, not a service.

```
knight_feature_analytics_reports/
├── knight_manifest.yaml
├── pyproject.toml
├── knight_feature_analytics_reports/
│   ├── apps.py            AppConfig with a KNIGHT hook
│   ├── models.py
│   ├── migrations/
│   ├── services.py
│   ├── views.py / serializers.py / urls.py
│   ├── settings_fragment.py   config contributed to the store
│   └── checks.py          self health check reported to KNIGHT
└── tests/
```

Rules:

- A Feature never contains customer-specific code, branding, or configuration.
- A Feature never edits store business code; it integrates through documented
  extension points (URL include, signals, settings fragment, admin registry).
- A Feature owns only its own models and migrations, in its own app label.
- A Feature is **not** a microservice ([`adr/0014`](adr/0014-features-as-deployable-packages.md) §Consequences).

## 5. Feature manifest

Machine-readable, validated by KNIGHT at publish time and by the store at
install time.

```yaml
apiVersion: knight.dev/v1
slug: analytics-reports
version: 1.4.0
name: Analytics Reports
architecture: in_process   # in_process | external_service; omitted means in_process
runtime: django            # closed list; omitted means django
django:                    # the block is named for the runtime above
  app_label: knight_analytics_reports
  installed_app: knight_feature_analytics_reports
  urls: { include: knight_feature_analytics_reports.urls, prefix: analytics/ }
compatibility:
  storeVersion: ">=4.0.0,<6.0.0"
  python: ">=3.12"
  django: ">=5.0,<6.0"
dependencies:
  features:
    - { slug: analytics-core, version: ">=1.2.0,<2.0.0" }
  python: ["pandas>=2.0,<3.0"]
migrations:
  required: true
  reversible: true          # declares whether down-migrations exist
  estimatedDurationSeconds: 30
  requiresMaintenanceWindow: false
  extensions: [pg_trgm]     # created before the migrations; never dropped
configuration:
  schema: config.schema.json    # JSON Schema, validated by KNIGHT
  defaults: { language: fa, schedule: daily }
  secrets: [analytics_api_key]  # names only; values never live in the package
install:
  strategy: package-install     # package-install | vendored | no-op
  requiresRestart: true
  healthCheck: knight_feature_analytics_reports.checks.health
uninstall:
  strategy: disable-then-remove
  dataRetentionDays: 30
```

The manifest is the contract. KNIGHT rejects a publish whose manifest fails
schema validation, whose digest does not match, or whose declared dependencies
cannot be resolved in the registry.

**`migrations.extensions`** names database extensions the Feature needs present
before its own migrations run. The list KNIGHT accepts is closed — `pg_trgm`,
`btree_gin`, `btree_gist`, `unaccent`, `citext`, `pgcrypto` — and declaring any
of them requires `compatibility.database: postgresql`. They are created
idempotently by their own step and are **never dropped**, by a rollback or by an
uninstall: an extension is shared with the store and with every other Feature in
the same database ([`adr/0031`](adr/0031-database-extensions-are-declared-not-migrated.md)).


### The runtime, and the three names every runtime shares

`runtime:` says which runtime the Feature is built for, and the block below it is
named for that runtime. Omitting it means `django`, because every manifest
written before phase 17 is a Django Feature and refusing them would have broken a
published contract to add a field they all imply
([`adr/0032`](adr/0032-a-feature-declares-its-runtime.md)).

A manifest carrying a block for a runtime it does not declare is **refused**:
that is an author who has copied a file.

The same Feature for a node store:

```yaml
runtime: node
node:
  namespace: knight_analytics_reports
  module: "@knight/feature-analytics-reports"
  mount: { export: router, prefix: analytics/ }
install:
  healthCheck: "@knight/feature-analytics-reports#health"
```

Both blocks say the same three things, and everything downstream of the reader —
the parsed manifest, the job payload, the store's installer — uses the neutral
names rather than either spelling:

| Neutral name | What it is | Django | Node |
|---|---|---|---|
| **namespace** | what this Feature's migrations and state are recorded under | `app_label` | `namespace` |
| **module** | what the store loads to get the code | `installed_app` | `module` |
| **mount** | the exported symbol serving routes, and its path | `urls.include` + `urls.prefix` | `mount.export` + `mount.prefix` |

Validation follows the runtime, because the spellings genuinely differ: an
`app_label` must be a Python identifier because it ends up in a Django migration
table, a node `module` must be a valid npm specifier because it ends up in an
`import`, and a callable is a dotted path for Django and `module#export` for
node. An author who writes the wrong one is told which shape was wanted.

A runtime is only added to the list once a store of that runtime has actually
taken delivery of a Feature — see
[`../stores/node-reference-store`](../stores/node-reference-store).
### The architecture, and Features that are not code

`architecture:` says whether the store runs this Feature's code at all, and it
sits **above** `runtime:`. `runtime` answers "what language is this code written
for" and only means anything when there is code; `architecture` answers "is there
code at all". Omitting it means `in_process`, because every manifest written
before phase 22 is code
([`adr/0033`](adr/0033-api-driven-features.md)).

An `external_service` Feature runs wherever its author runs it, once, for every
store. What KNIGHT delivers is a **signed configuration document** rather than an
archive:

```yaml
apiVersion: knight.dev/v1
slug: subscriptions
version: 2.0.0
name: Subscriptions and Recurring Orders
architecture: external_service

service:
  base_url: https://subscriptions.knight.dev   # absolute; https outside development
  auth: hmac-sha256                            # hmac-sha256 | bearer-token
  health: /healthz
  secret: SUBSCRIPTIONS_SERVICE_SECRET         # the NAME of the secret, never its value

webhooks:                                      # events the store forwards
  - event: order.placed                        # must be in the store's own catalogue
    path: /hooks/order-placed
    delivery: at-least-once                    # at-least-once | at-most-once

api_proxies:                                   # routes the store forwards
  - prefix: subscriptions/                     # in the store's own URL space
    upstream: /api/v1/subscriptions/
    methods: [GET, POST]                       # defaults to [GET]
    identity: customer                         # anonymous | customer | staff

ui_mounts:                                     # where its screens hang
  - slot: admin.sidebar                        # must be a slot the store offers
    label: Subscriptions
    path: /admin/subscriptions
    kind: iframe                               # iframe | redirect

configuration:
  defaults: { retry_attempts: 3 }
```

An `external_service` manifest carrying `runtime`, `django`, `node`, `dotnet`,
`migrations`, `install`, `dependencies` or `workers` is **refused**. Those blocks
only mean something for code the store runs, and a `migrations:` block is the
most dangerous of them, because it reads like a promise that something will be
migrated and nothing will.

A manifest declaring none of `webhooks`, `api_proxies` or `ui_mounts` is also
refused: installing it would do nothing at all, which is a manifest somebody has
not finished.

**It is still signed.** The configuration *is* the artifact — same digest, same
detached ECDSA signature over the same ASCII digest string, same `fetch` then
`verify` before the store acts on any of it. A document that tells a store where
to forward its customers' requests is not a thing to take on trust because it
happens to be JSON.

**What each side validates.** KNIGHT checks the *shape* of an event name and a
slot at publish, because it cannot know what any particular store emits or
offers. The store checks the *name* at install, against its own catalogue,
because it is the only thing that can — without which a Feature subscribing to
`order.plaecd` installs cleanly, passes its health check and never hears
anything.

**What the store does with it.** It registers the subscriptions in its own event
bus, mounts the proxy prefixes in its own URL space, and records the mounts for
its own interface. Forwarded requests carry a signed assertion of *who is asking*
and **none of the shopper's own credentials**: no session cookie, no
`Authorization` header, no CSRF token. The store decides `anonymous` / `customer`
/ `staff` itself, forwards only the declared methods, and refuses to return a
`Set-Cookie` — a Feature's service must not be able to issue a session on the
store's origin.

## 6. Installation state machine

```
        NotInstalled
             │ install job accepted
             ▼
          Pending ──────────────► Failed ◄──────────────┐
             │ agent picked up      ▲                   │
             ▼                      │ any step fails    │
         Installing ────────────────┤                   │
             │ migrate+configure+enable+healthy         │
             ▼                                          │
         Installed ◄──── Updating ───────────────────────┤
             │  ▲            │ upgrade job                │
             │  │            └──► RollingBack ────────────┘
             │  │ re-enable          │ restored
             ▼  │                    ▼
          Disabled              Installed (previous version)
             │ uninstall job
             ▼
        Uninstalling ──► NotInstalled
```

Legal transitions only; anything else is rejected by the aggregate, not by the
caller. Terminal-ish states (`Failed`, `Disabled`) always have a defined way
out (retry, rollback, re-enable, uninstall).

`Failed` never silently blocks billing or hides the entitlement — it raises an
alert and, if it recurs, opens an incident.

## 7. Installation job pipeline

```
KNIGHT                                    Store (integration layer / agent)
──────                                    ─────────────────────────────────
entitlement granted / admin action
   │
   ├─ resolve dependencies + compatibility
   ├─ create FeatureInstallationJob (queued, idempotent)
   ├─ mint a short-lived, single-job install token
   │                                      ◄── agent polls for its jobs
   │                                          (outbound only; no inbound port)
   │                                      ├─ 1 preflight  (versions, disk, deps)
   │                                      ├─ 2 fetch package + verify digest+signature
   │                                      ├─ 3 backup point (db snapshot ref, current versions)
   │                                      ├─ 4 install package
   │                                      ├─ 5 create declared extensions
   │                                      ├─ 6 apply migrations
   │                                      ├─ 7 apply configuration
   │                                      ├─ 8 enable in the app registry
   │                                      ├─ 9 reload/restart if required
   │                                      └─ 10 health check
   │  ◄── step-by-step progress reports ──┘
   └─ on success: Installed · on failure: rollback → Failed, alert, audit
```

Properties:

- Every job is **idempotent** and identified by a job id; a retried step never
  double-applies.
- Progress is reported per step (`Migration 2/4`) so the dashboard can show it.
- A job has a timeout and a bounded retry policy with backoff.
- Only one job at a time per store; jobs queue per store.
- Every step and outcome is audited with a correlation id.
- Extensions are created **before** the migrations and are never undone. The
  privilege to create one is the privilege a store's database user most often
  lacks, and this ordering means that failure arrives before the schema has been
  touched, naming the statement an administrator must run
  ([`adr/0031`](adr/0031-database-extensions-are-declared-not-migrated.md)).


### The external-service pipelines

A Feature that is a service gets a different step list, built entirely from the
verbs above:

| Job | Steps |
|---|---|
| Install / upgrade | `preflight` `fetch` `verify` `backup` `configure` `install` `enable` `healthcheck` |
| Rollback | `restore-package` `configure` `enable` `healthcheck` |
| Uninstall | `disable` `backup` `remove-package` |
| Enable / disable / configure | `enable` `healthcheck` / `disable` / `configure` `healthcheck` |

`install` means "make this Feature present in this store", which for a service is
registering its webhooks and wiring its proxy routes — the same relationship
every runtime already has to that verb.

**No verb here is new**, and that is deliberate rather than tidy. A store that
meets a step it does not know refuses the whole job, and stores are upgraded on
their own schedule: adding a verb would have broken every store that had not
caught up, on the day it shipped, for exactly the Features that most needed to
work. There is a test in the backend suite that fails if an external pipeline
ever names a verb the in-process one does not.

Four steps are absent, and each absence is a fact about the architecture rather
than a shortcut: **no `create-extensions`** because there is no database to
create one in, **no `migrate`** and **no `reverse-migrate`** because there is no
schema in the store, and **no `reload`** because nothing was loaded into the
store's process. `backup` stays, and keeps the *registration* rather than a
package tree, so a rollback has something local to restore.

## 8. Dependency resolution and compatibility

Before a job is created, KNIGHT resolves:

1. **Feature dependencies** — transitively, from manifests
   (`AI Reports → Analytics Core`). Result is an ordered install plan.
2. **Store compatibility** — `compatibility.storeVersion` against the store's
   reported version; `python`/`django` against the store's reported runtime.
3. **Conflicts** — an already-installed version outside a dependency's allowed
   range is a hard failure with an explanation, never a forced upgrade.
4. **Infrastructure** — a Feature requiring dedicated infrastructure is refused
   on `SharedManaged` hosting.

If resolution fails, **no job is created**; the dashboard shows exactly which
constraint failed. Entitlement may still be granted (the customer paid) with
installation left `NotInstalled` and a blocking reason recorded.

Install order is the topological order of the resolved plan. Uninstall order is
its reverse, and a Feature that another installed Feature depends on cannot be
uninstalled.

## 9. Configuration

```
FeatureConfiguration   storeId, featureId, values (jsonb), version, updatedBy, updatedAt
```

- Validated against the manifest's JSON Schema before it is stored or sent.
- Customer-specific values live in KNIGHT, never in the package.
- Secrets referenced by name in the manifest are stored encrypted at rest in
  KNIGHT and delivered only over the authenticated install/config channel;
  they are never logged, never returned by a read API, and never written to a
  job record.
- A configuration change is its own job type (`ApplyConfiguration`) — cheap,
  no migration, health-checked, audited.

## 10. Upgrade and rollback

**Upgrade** is an `Updating` job: same pipeline, plus a compatibility check of
the new version against the store and against dependent Features.

**Rollback** is attempted automatically when a step fails, in reverse order of
what succeeded. It is honest about limits:

- Package downgrade and configuration restore are always attempted.
- Database rollback is attempted **only** if the manifest declares
  `migrations.reversible: true` and the reverse migrations exist.
- If a migration is irreversible and has already applied, KNIGHT does **not**
  guess. It stops, marks the installation `Failed` with
  `rollbackOutcome = ManualInterventionRequired`, raises a critical incident,
  and records exactly which migration is the boundary.

See [`adr/0016`](adr/0016-feature-migration-and-removal-policy.md).

## 10.1 Staged rollout across the fleet

Upgrading one store is reviewed and reversible. Moving **every** store onto a new
version is the operation R16 in [`risks.md`](risks.md) is about, and it is a
first-class object rather than a loop: a **rollout**, made of ordered **waves**
of stores.

```
rollout: advanced-promotions 1.1.0, threshold 2
  wave 0  canary      1 store    -> must succeed
  wave 1  50%         12 stores
  wave 2  the rest    13 stores
```

The rules, all enforced by the aggregate rather than by whoever is driving it
([`adr/0028`](adr/0028-staged-rollouts-with-a-single-store-canary.md)):

- **The canary is exactly one store**, never a percentage, and a non-production
  one where the fleet has one.
- **A wave does not begin until the previous wave has reported on every store.**
- **A failed canary halts the rollout whatever the threshold says.**
- **Failures are counted across the whole rollout**, not per wave.
- **A rollout sequences; it does not install.** Each store gets an ordinary
  upgrade job, so a rollout can ask an agent for nothing a hand-made upgrade
  could not.

| Action | What it does | What it deliberately does not do |
|---|---|---|
| Halt | Queues nothing further | Does not cancel a job already running inside a store — interrupting a migration half-way is worse than letting it finish |
| Resume | Accepts the failures so far and continues | Does not clear them; the next failure halts it again |
| Cancel | Ends the rollout | Does not downgrade stores already upgraded — a rollout is not a transaction |

A rollout only ever targets stores that **already have the Feature** on a
different version. Installing a Feature somewhere for the first time is an
entitlement decision, and a version bump must never quietly become one.

At most one rollout per Feature may be live at a time.

Routes: `POST /api/v1/rollouts` (plan), `/{id}/start`, `/{id}/halt`,
`/{id}/resume`, `/{id}/cancel`. All require `feature.publish` — a rollout crosses
customers, and no customer-scoped role holds that permission.

## 11. Removal semantics

"Turning a feature off" is four different operations. They are never conflated:

| Operation | Code | Data | Trigger |
|---|---|---|---|
| **Disable** | stays installed | kept | customer toggles off; entitlement lapses |
| **Uninstall** | removed | kept for `dataRetentionDays`, then purged | explicit admin/customer action |
| **Rollback** | previous version restored | per reversibility | failed upgrade |
| **Purge** | already removed | deleted | retention expiry or explicit request |

**Default policy on entitlement loss (expiry, downgrade, cancellation):
`Disable`, not uninstall.** Data is retained for the manifest's retention
window so a customer who renews loses nothing. Uninstall is deliberate,
audited, and warns about data consequences before it runs.

## 12. Store provisioning

Automated provisioning uses exactly the same machinery — see
[`store-provisioning.md`](store-provisioning.md).

```
Customer created → plan selected → base store provisioned → base Features
resolved from the plan → installation jobs → configuration → health check → Store Ready
```

Base plan Features are not special-cased; they are ordinary registry Features
installed by ordinary jobs.

## 13. Professional plan

Identical delivery model. The only difference is infrastructure isolation
(dedicated server/environment). A dedicated store receives the same packages
through the same pipeline — never a hand-built variant.

## 14. Observability

Every installation is a first-class observable object: current state, current
step and progress, duration, log tail, resulting health, and the incident it
raised if it failed. See `observability.md` §10.

## 15. Non-goals

- KNIGHT does not compile or build Feature code at delivery time; it delivers
  pre-built, signed artifacts.
- KNIGHT does not run arbitrary commands on a store — the agent exposes only
  the narrow, named operations of this pipeline.
- Features do not become network services.
- Feature source code is never duplicated per customer, in any workflow.
