# KNIGHT Target Architecture

Status: **authoritative**. Supersedes `architecture/platform-overview.md`.

## 1. System context

```
   ┌────────────┐        ┌─────────────┐        ┌──────────────┐
   │ Platform   │        │  Customer   │        │  Support /   │
   │ Super Admin│        │   Owner     │        │  Developer   │
   └─────┬──────┘        └──────┬──────┘        └──────┬───────┘
         │  HTTPS (dashboard)   │                      │
         └──────────────┬───────┴──────────────────────┘
                        ▼
              ┌───────────────────────┐
              │        KNIGHT         │  control plane
              │  ASP.NET Core API     │
              │  PostgreSQL + Redis   │
              └───┬───────────┬───────┘
        outbound  │           │  inbound (store/agent push + job polling)
        (pull)    ▼           ▼
        ┌──────────────┐   ┌──────────────┐
        │ Store Mgmt   │   │ KNIGHT Agent │
        │ API (Django) │   │ (server)     │
        └──────────────┘   └──────────────┘
```

KNIGHT never reaches into a store's database, and never serves store traffic.

### Feature delivery view

KNIGHT is not only an observer — it **delivers software** to stores. A Feature
is implemented once, packaged, registered, and installed automatically into
every entitled store ([`feature-delivery.md`](feature-delivery.md)).

```
                    KNIGHT
                       │
              Feature Registry          (Feature + FeatureVersion + manifest)
                       │
              Deployment Engine         (resolve → job → track → verify)
                       │
              Feature Package           (signed, digest-verified artifact)
                       │
     ┌─────────────────┼─────────────────┐
     ▼                 ▼                 ▼
  Store A           Store B           Store C          Django applications
     │                 │                 │
 ┌───┴────┐        ┌───┴────┐        ┌───┴────┐
 │Feature │        │Feature │        │Feature │        installed feature modules
 │modules │        │modules │        │modules │        (inside the store process)
 └────────┘        └────────┘        └────────┘
     +                 +                 +
 store business    store business    store business     owned by the store, untouched
   logic             logic             logic
```

The commercial and technical chains are distinct and both required:

```
Subscription → Entitlement → Installation Job → Feature Module (running, healthy)
```

## 2. Containers

| Container | Technology | Responsibility |
|---|---|---|
| KNIGHT API | ASP.NET Core (.NET 10) | Control-plane REST API, SignalR hub, background workers |
| KNIGHT DB | PostgreSQL | Control-plane data only |
| KNIGHT Cache | Redis | Caching, rate limiting, short-lived tokens, idempotency |
| KNIGHT Dashboard | React + Vite + TypeScript | Administrative UI (RTL, responsive) |
| Store App | Django + DRF | One per customer store; owns its own business domain and DB |
| Store DB | PostgreSQL | One per store; KNIGHT has no access |
| KNIGHT Agent | small daemon on each server | Reports server health **and** executes typed feature-lifecycle jobs (install/upgrade/configure/rollback) |
| Package Registry | private artifact store | Holds signed, immutable Feature packages and base store images |

## 3. Modules (modular monolith)

The KNIGHT API stays a **modular monolith**. Modules have clear boundaries and
communicate through in-process abstractions, not HTTP.

```
Identity        users, sessions, tokens, password policy
AccessControl   roles, permissions, policy evaluation
Customers       the paying customer (company/person)
Stores          store registry, lifecycle, integration credentials, version
Plans           plans, plan-features, pricing rules
Subscriptions   subscription state, selected features, entitlements
FeatureRegistry features, feature versions, manifests, packages, dependencies,
                compatibility, publishing and yanking
FeatureDelivery installation state per store, dependency resolution, install
                plans, jobs and job steps, configuration delivery, rollback
Provisioning    store provisioning jobs reusing the delivery pipeline
Billing         invoices, billing periods, price calculation results
Servers         infrastructure metadata, agents, hosting model
Monitoring      health checks, metrics ingestion, status evaluation
Errors          error ingestion, fingerprinting, error groups
Incidents       incidents, incident events, alerts
Logs            structured log ingestion/forwarding and query facade
Notifications   channels, delivery, notification preferences
Audit           audit trail of administrative actions
Reports         aggregate reporting read models
```

### Dependency rules

- A module may depend on `Knight.Domain` and `Knight.Application`.
- A module must **not** depend on another module's internals; cross-module use
  goes through an interface published by the owning module.
- A module must **not** depend on `Knight.Infrastructure`.
- `Knight.Api` composes everything and owns HTTP/SignalR concerns only.

These rules are already enforced by `Knight.ArchitectureTests` and must stay
enforced after the pivot.

## 4. Layering

```
React Dashboard
      │  REST (JSON) + SignalR
      ▼
Knight.Api            endpoints, auth policies, DTO mapping, validation
      ▼
Modules               domain + application services (business rules)
      ▼
Knight.Infrastructure EF Core, Redis, HTTP clients to stores, storage
      ▼
PostgreSQL / Redis / Store Management APIs
```

Database entities are never returned directly from endpoints; contracts live
in `Knight.Contracts` and are versioned (`/api/v1/...`).

## 5. Communication patterns

| Direction | Mechanism | Notes |
|---|---|---|
| Dashboard → KNIGHT | REST over HTTPS, JWT access token | Versioned, ProblemDetails errors |
| KNIGHT → Dashboard | SignalR | Status changes, new incidents, server offline |
| KNIGHT → Store | HTTPS pull (`/api/knight/health`, `/version`, `/features`) | Scheduled by a background worker |
| Store → KNIGHT | HTTPS push (errors, events, deployment notices) | Authenticated with a store token |
| Agent → KNIGHT | HTTPS push of metrics/heartbeat | Authenticated with an agent token |
| Agent ← KNIGHT | Agent **polls** for typed lifecycle jobs (outbound only) | No inbound port on the store; no command strings — see [`adr/0015`](adr/0015-feature-delivery-mechanism.md) |
| Agent → Package Registry | HTTPS fetch of a signed artifact | Digest + signature verified before installation |

No message broker is introduced initially. A broker becomes justified only
when ingestion volume or asynchronous fan-out demands it (see `risks.md`).

## 6. Hosting models

A store's hosting is modelled independently from its plan:

```
HostingModel: SharedManaged | DedicatedManaged | CustomerManaged
```

`Professional` plan customers normally receive `DedicatedManaged`, but KNIGHT
must never infer capability from the plan alone — the `Server`/`Infrastructure`
record is the source of truth for where an application runs.

Hosting affects **isolation only, never the delivery model**: a dedicated store
receives the same signed packages through the same pipeline as a shared one.
A Feature is never hand-built for a customer.

## 7. Real-time flow

```
Agent heartbeat missed  →  Monitoring evaluates  →  ServerOffline domain event
   →  Incident created  →  Notification dispatched  →  SignalR push  →  Dashboard badge
```

## 8. Environments

`Development`, `Staging`, `Production` are explicit first-class values on both
KNIGHT and every store registration. A store registered as `Production` must
refuse to talk to a non-production KNIGHT and vice versa; environment mismatch
is a hard authentication failure, not a warning.

## 9. Domain events (in-process)

```
StoreRegistered          StoreProvisioned        SubscriptionChanged
FeatureEntitlementGranted  FeatureEntitlementRevoked
FeaturePublished         FeatureVersionYanked
FeatureInstallationRequested  FeatureInstalled   FeatureInstallationFailed
FeatureUpgraded          FeatureRolledBack       FeatureDisabled  FeatureUninstalled
FeatureConfigurationChanged
ServerOffline            ServerRecovered         ErrorDetected
IncidentCreated          IncidentResolved        DeploymentCompleted   BackupFailed
```

Handled in-process initially. The contracts are designed so they can later be
serialised onto a broker without changing producers.

## 10. What is deliberately excluded for now

Kubernetes, service mesh, Kafka, CQRS everywhere, event sourcing, per-CRUD
microservices, a custom log storage engine, and a remote-shell agent. Each may
be revisited only with a recorded justification.

A **Feature Package is not a microservice**: it is a Django app installed into
the store's own process. "One store application with many installable
features", never "one store with twenty services"
([`adr/0014`](adr/0014-features-as-deployable-packages.md)).
