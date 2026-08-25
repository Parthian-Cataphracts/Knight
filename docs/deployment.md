# Deployment

Status: **authoritative proposal**.

## 1. Environments

`Development`, `Staging`, `Production` are fully separate: separate databases,
separate secrets, separate signing keys, separate store registrations. A store
never crosses environments (`store-integration.md` §6).

## 2. Local development topology

```
docker compose (infrastructure/docker)
├── knight-postgres      control-plane database
├── knight-redis         cache / rate limiting / nonces
├── knight-api           ASP.NET Core (or run from the SDK)
├── knight-dashboard     Vite dev server
├── store-postgres       reference Django store database
├── store-redis
└── reference-store      Django + knight_integration
```

The reference store exists so KNIGHT↔Store integration can be exercised
locally end to end. Store containers are separate services with separate
databases — never a schema inside the KNIGHT database.

## 3. Feature delivery pipeline

KNIGHT deploys two different things, and they must not be confused:

```
KNIGHT itself      built and deployed by our CI/CD (this repository)
Feature packages   built, signed, published, then delivered to stores by KNIGHT
```

Feature build-and-publish pipeline:

```
feature repo/dir
   → test (unit + against a reference store)
   → build wheel/sdist
   → validate knight_manifest.yaml against the manifest schema
   → sign artifact (detached signature over the sha256 digest)
   → upload to the private package registry
   → register the FeatureVersion in KNIGHT (packageReference + digest + signature + manifest)
   → publish (makes it installable)
```

Delivery to a store is then entirely KNIGHT's job: resolve → create job →
agent pulls → install → migrate → configure → enable → health check
([`feature-delivery.md`](feature-delivery.md) §7). Nothing is copied into a
store by hand, in any environment.

The **package registry** is a private, authenticated artifact store (private
PyPI index or object storage with an index). Agents may only read from it, only
over TLS, and only artifacts whose digest and signature match the
`FeatureVersion` record.

Base store images are published through the same pipeline
([`store-provisioning.md`](store-provisioning.md) §3).

## 4. Runtime deployment

```
Internet
   │
Reverse proxy (TLS termination, HSTS, rate limiting)
   ├── knight.example.com          -> dashboard static bundle
   ├── api.knight.example.com      -> KNIGHT API (dashboard + ingestion + agent paths)
   └── cafe1.ir, cafe2.ir, ...     -> independent store deployments
```

<<<<<<< HEAD
The dashboard is a static bundle (any static host or the reverse proxy). The
API is a container. Stores are deployed independently, on shared or dedicated
servers, and are never redeployed by KNIGHT in the initial phases.
=======
The dashboard is a static bundle (any static host or the reverse proxy). Stores
are deployed independently, on shared or dedicated servers, and are never
redeployed by KNIGHT in the initial phases.

Two hostnames is the topology to reach for when the API needs an address that
does not move when the dashboard does. It is not the only valid one: a single
hostname routing by path — `/` to the bundle, `/api/v1`, `/hubs`, `/artifacts`
and `/health` to the API — costs one DNS record and one certificate instead of
two, and removes cross-origin from the picture entirely, which is worth
something given that a CORS mistake is invisible to every test that is not a
browser. That is what [`installation.md`](installation.md) deploys.
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5

## 5. Configuration

Standard .NET configuration precedence: `appsettings.json` →
`appsettings.{Environment}.json` → environment variables → secret store.
<<<<<<< HEAD
`appsettings.Development.json` contains placeholders only.

Required settings:

```
ConnectionStrings__KnightDb
ConnectionStrings__Redis
Knight__Environment                Development|Staging|Production
Knight__Jwt__Issuer / Audience / SigningKey (or KeyPath)
Knight__Jwt__AccessTokenMinutes / RefreshTokenDays
Knight__Cors__AllowedOrigins
Knight__Ingestion__MaxBatchSize / RateLimitPerMinute
Knight__StorePolling__IntervalSeconds / TimeoutSeconds
Knight__Registry__BaseUrl / Credentials
Knight__Registry__SigningPublicKey        (agents verify against this)
Knight__Jobs__DefaultTimeoutSeconds / MaxAttempts / PollIntervalSeconds
Knight__Otel__Endpoint (optional)
```

Dashboard build-time config: `VITE_API_BASE_URL`, `VITE_SIGNALR_URL`,
`VITE_DEFAULT_LOCALE`.
=======
`appsettings.Development.json` contains placeholders only. Environment variables
use `__` where the setting path uses `:`.

**Required. The host refuses to start in Production without them:**

```
ASPNETCORE_ENVIRONMENT               Development | Staging | Production
ConnectionStrings__ControlPlane      the control-plane database
ConnectionStrings__Redis             required outside Development (adr/0020)
Jwt__SigningKey                      >= 32 characters; a placeholder is refused
Stores__IntegrationSigningKey        >= 32 characters, and NOT the Jwt key
```

**Required for anything to reach it, or for a Feature to be installable:**

```
ASPNETCORE_URLS                      where Kestrel listens, behind the proxy
ForwardedHeaders__KnownProxies__N    every proxy in front, beyond the loopback
Cors__AllowedOrigins__N              only when the dashboard is a separate origin
FeatureArtifacts__ArtifactRoot       the package store
FeatureArtifacts__PublicBaseUrl      what a download URL is minted against
FeatureArtifacts__ActiveKeyId        the key new versions are signed with
FeatureArtifacts__Keys__<id>__PublicKey   what a signature is checked against
```

**Everything else has a working default** and is listed with its meaning in
`appsettings.json`: `Jwt__Issuer`/`Audience`/lifetimes, `ControlPlaneAccess__*`,
`Stores__*` (rotation, token lifetime, domain verification, probe),
`Ingestion__*` (batch caps, per-store limit), `RateLimiting__*`,
`FeatureDelivery__*` (job claim timeout, attempts), `Subscriptions__*`,
`Billing__*`, `Provisioning__*`, `Servers__*`, `Observability__*`,
`Retention__*`, `Storage__LocalRootPath`, `Catalogue__SeedPath`, and
`Telemetry__{Enabled,OtlpEndpoint,ServiceName,SampleRatio,TraceDatabase}`.

Outbound mail is off unless `Email__Host` and `Email__FromAddress` are set; a
deployment without them falls back to a one-time password and says so.
`Email__DashboardBaseUrl` has to be set with them, because KNIGHT cannot infer
from an inbound request where the recipient will be able to reach it.

**Dashboard build-time config** is `VITE_USE_MOCKS`, `VITE_DEFAULT_LOCALE`, and
optionally `VITE_API_BASE_URL` and `VITE_SIGNALR_URL`. Leave the last two unset
where the dashboard and the API share an origin: the bundle then addresses
`/api/v1` and `/hubs/control-plane` relatively, which is one less thing to
rebuild when a hostname changes and one less way to end up with a bundle that
only works over the scheme it was built for.

### The reverse proxy

TLS terminates at the proxy, so requests reach the host over plain HTTP from the
proxy's address. `X-Forwarded-Proto` and `X-Forwarded-For` are read — but only
from an address the deployment names. The loopback is trusted by default
(a proxy on the same machine); anything else has to be listed in
`ForwardedHeaders__KnownProxies`, or the caller's real address never reaches the
rate limiters and the audit trail records the proxy instead.

The proxy must also pass `Upgrade` and `Connection` through on the hub path, or
SignalR silently falls back to long polling.

### Installing it

[`installation.md`](installation.md) is the worked version of this section: one
command that installs a single-hostname deployment on Ubuntu or Debian, sets
every value above, and stays out of the way of anything else on the machine.
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5

## 6. Secrets

- Never committed, never logged, never returned by an API.
- Store client secrets and agent tokens are stored **hashed**; the plaintext is
  displayed exactly once at creation.
- JWT signing keys and connection strings come from the environment or a secret
  store, and are rotatable without a code change.
- A pre-commit/CI secret scan is part of the pipeline.

## 7. Database migrations

### KNIGHT's own schema

- All schema change is EF Core migrations, reviewed in the PR that needs them.
- Migrations run as an explicit deployment step, not silently at startup in
  Production. The step is
  `dotnet run --project backend/tools/Knight.Bootstrap -- --migrate-only`, which
  applies the migrations and reconciles the seeded roles and catalogue without
  creating an account or prompting for anything. It is idempotent, and CI proves
  that by running it twice and requiring the second run to be a no-op — a deploy
  runs it on every release, and most releases have no migration to apply.
- Expand/contract for anything destructive: add → backfill → switch → remove in
  a later release.
- High-volume tables (`ServerMetric`, `ErrorEvent`, `LogEntry`) are partitioned
  by time from the first migration that creates them.

### Feature (store-side) migrations

Feature migrations are Django migrations owned by the feature package and run
**inside the customer's database** by an installation job. They follow the same
expand/contract discipline and are governed by
[`adr/0016`](adr/0016-feature-migration-and-removal-policy.md): declared
reversibility in the manifest, a recorded restore point where possible,
step-by-step progress reporting, and an explicit
`ManualInterventionRequired` outcome instead of a guessed rollback.

## 8. CI/CD pipeline (target)

```
lint (dotnet format, eslint, prettier)
  -> build (dotnet build, vite build, tsc --noEmit)
  -> test (unit, integration incl. PostgreSQL, architecture, vitest)
  -> security (dependency audit, secret scan)
  -> migration validation (migrations apply cleanly to a fresh DB)
  -> docker build + push
  -> (feature repos) build + manifest validation + sign + publish to registry
     + register FeatureVersion in KNIGHT
  -> deploy staging -> smoke tests -> deploy production (manual approval)
```

Platform is not yet chosen; the stages are.

## 9. Rollback

Two independent rollback stories:

| What | How |
|---|---|
| KNIGHT (API/dashboard) | redeploy the previous image/bundle |
| A Feature in a store | rollback job: downgrade package, restore config, reverse migrations only if declared reversible ([`adr/0016`](adr/0016-feature-migration-and-removal-policy.md)) |

The API and dashboard roll back by redeploying the previous image/bundle.
Migrations are forward-only; a rollback must be paired with a compensating
migration, which is why destructive changes use expand/contract.

## 10. Backups

Nightly `pg_dump` of the KNIGHT database. The scripts are in
[`infrastructure/scripts/`](../infrastructure/scripts/) and the procedure — take,
verify, drill, and restore for real during an incident — is
[`runbooks/restore-drill.md`](runbooks/restore-drill.md).

The restore drill is **a CI job, not a calendar entry**: it runs on every push,
takes a real backup, restores it into a scratch database and compares the table
list, every row count, the migration history and the constraints and indexes
([`adr/0027`](adr/0027-the-restore-drill-is-the-backup-test.md)). Every dump
carries a manifest with its SHA-256, and a restore refuses a dump whose checksum
does not match.

Still deployment configuration rather than code, and named here so it is not
mistaken for done: the nightly schedule itself, the offsite copy of each dump,
and how long copies are kept.

Store backups are the store's responsibility; KNIGHT only records the reported
backup status and raises `backup.failed` alerts
([`adr/0026`](adr/0026-knight-records-backups-it-does-not-take-them.md)).

## 11. Not yet

Kubernetes, autoscaling, multi-region, blue/green. Introduced only when a
measured requirement exists.
