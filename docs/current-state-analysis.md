# Current State Analysis

<<<<<<< HEAD
Snapshot date: **2026-08-18**. Commit: `39fe127` ("Initial commit: multi-tenant
food service platform"). This is the only commit in the repository.

## 1. Summary

The repository contains a well-built implementation of **a product that the
KNIGHT specification forbids**: a single shared ASP.NET Core application that
owns the business logic and data of every customer store, in one PostgreSQL
database with tenant-scoped rows.

The KNIGHT specification requires the opposite: stores are independent Django
applications with their own databases, and KNIGHT only manages and observes
them.

The existing code is not worthless — its Identity, authorization, audit,
feature-management, correlation, and testing infrastructure are directly
reusable by the control plane. Its business modules are not.

## 2. Repository layout

```
knight/
├── backend/            .NET 10 solution (Knight.slnx)
│   ├── src/            Api, Application, Contracts, Domain, Infrastructure
│   ├── modules/        11 business/platform modules
│   ├── tests/          UnitTests, IntegrationTests, ArchitectureTests
│   ├── tools/          Knight.Bootstrap
│   └── docs/           2 stray docs (ADR 0008 duplicate + checkout orchestration)
├── frontend/           EMPTY — README + .gitkeep only
├── infrastructure/     docker compose, database/storage/reverse-proxy notes
└── docs/               architecture, adr (9), api, database, security
```

Total tracked files: ~544.

## 3. Backend inventory

### 3.1 Core projects (`backend/src`)

| Project | Purpose | Pivot verdict |
|---|---|---|
| `Knight.Api` | Minimal-API host, 37 endpoint files, middleware, auth policies, composition | **Reuse the host; replace endpoints** |
| `Knight.Application` | Abstractions: auditing, features, identity, tenancy, time; authorization; exceptions | **Reuse mostly** |
| `Knight.Contracts` | DTOs per module (AccessControl, Auth, Catalog, Checkout, Common, Customer, Delivery, Fulfillment, Health, Ordering, Payment, Platform, Promotions) | **Reuse Common/Auth/Health/AccessControl; retire the rest** |
| `Knight.Domain` | Shared domain primitives and exceptions | **Reuse** |
| `Knight.Infrastructure` | EF Core persistence, migrations, repositories, caching, auditing, security, storage, health checks | **Reuse the patterns; new DbContext and migrations** |

### 3.2 Modules (`backend/modules`)

| Module | What it does | Pivot verdict |
|---|---|---|
| `Identity` | Users, password hashing, access/refresh tokens, sessions | **Keep** — becomes KNIGHT identity |
| `Tenancy` | Tenant aggregate, tenant domains, lifecycle (Pending/Active/Suspended/Archived), tenant resolution | **Transform** — becomes `Customers` + `Stores` |
| `FeatureManagement` | Feature definitions, per-tenant feature state, feature access service | **Keep and extend** — becomes *entitlements*. Note: it models features as flags only; the registry/packaging/installation model ([`feature-delivery.md`](feature-delivery.md)) is entirely new |
| `Catalog` | Products, categories, variants, modifiers, media | **Migrate to Django store template** |
| `Customer` | End-consumer records of a store | **Migrate to Django store template** |
| `Ordering` | Orders | **Migrate to Django store template** |
| `Checkout` | Idempotent checkout orchestration | **Migrate to Django store template** |
| `Payment` | Payment obligations and attempts | **Migrate to Django store template** |
| `Promotions` | Promotions and coupons | **Migrate to Django store template** |
| `Fulfillment` | Fulfillment methods and settings | **Migrate to Django store template** |
| `Delivery` | Delivery zones, delivery quoting | **Migrate to Django store template** |

Seven of eleven modules are store business logic living inside the control
plane. That is the central violation to be undone.

### 3.3 Cross-cutting mechanisms already implemented and worth keeping

- Request pipeline: `CorrelationId → ExceptionHandling → CORS → RateLimiter → Authentication → TenantResolution → Authorization → Endpoints`
- Problem Details error contract with correlation id on every response
- Named rate-limiting policies (`platform`, `tenant-public`, `auth`)
- Audit recorders per module (`*AuditRecorder.cs`)
- Two operating contexts kept strictly separate: platform admin vs tenant user
- Health endpoints `/health/live`, `/health/ready`, exempt from tenant resolution
- OpenAPI + Scalar in Development
- Architecture tests enforcing module dependency rules

### 3.4 Tests

97 test files across three projects:

- `Knight.UnitTests` — 13 areas (Authorization, Catalog, Checkout, Customer, Delivery, FeatureManagement, Identity, Ordering, Payment, Persistence, Promotions, Security, Tenancy)
- `Knight.IntegrationTests` — 11 areas including a PostgreSQL-backed `Security` suite for tenant isolation (requires Docker)
- `Knight.ArchitectureTests` — enforces dependency rules

The isolation/authorization test *style* is exactly what the control plane
needs for its own customer-isolation tests, even though the subjects change.

### 3.5 Persistence

Single `PlatformDbContext`, EF Core migrations through
`20260816090000_FixPromotionCouponUpdatedAtNullability`. All tenant data is
row-scoped in one database — explicitly listed as an anti-pattern in the
specification when applied to independent stores.

## 4. Frontend inventory

Nothing exists. `frontend/` contains only:

- `README.md` describing a *planned* Next.js layout (`super-admin/`, `tenants/<slug>/{storefront,admin}`, `shared/*`)
- `.gitkeep` placeholders

Per [`adr/0011`](adr/0011-react-vite-dashboard.md) the target is a single
**React + Vite + TypeScript** RTL dashboard under `frontend/knight-dashboard/`.
The `tenants/` and `storefront` concepts do not belong to KNIGHT at all —
storefronts belong to the independent Django stores.

## 5. Infrastructure inventory

`infrastructure/` contains Docker Compose for local PostgreSQL and Redis, plus
README notes for database, storage, reverse-proxy, and scripts. No Django
store, no agent, no monitoring stack, no dashboard service.

## 6. Documentation inventory

9 ADRs (with two numbering collisions: two `0006` and two `0007` files) and
architecture/api/database/security docs — all describing the previous product.
Two additional docs are stranded under `backend/docs/` instead of `docs/`.

## 7. Gap list against the KNIGHT specification

| Specification area | Status |
|---|---|
| Identity, users, roles, permissions | Partially exists (tenant-shaped, needs reshaping) |
| Customers | Does not exist (closest analogue: `Tenancy`) |
| Stores (registration, lifecycle, integration status, version) | Does not exist |
| Plans, PlanFeature, FeaturePrice | Only raw feature flags exist; no plans, no pricing |
| Feature registry (Feature, FeatureVersion, manifest, packages) | Does not exist |
| Feature delivery (jobs, installer, dependency/compatibility resolution, rollback) | Does not exist |
| Feature packages + package registry + signing | Does not exist |
| Store provisioning automation | Does not exist |
| Subscriptions, SubscriptionFeature | Does not exist |
| Billing | Does not exist |
| Servers, infrastructure metadata | Does not exist |
| Monitoring, health aggregation, metrics | Only self health checks |
| Errors, error groups, fingerprinting | Does not exist |
| Incidents, alerts | Does not exist |
| Logs aggregation | Does not exist |
| Notifications | Does not exist |
| Audit logs | Exists per module; needs a central audit read model |
| KNIGHT↔Store contract | Does not exist |
| Django store template + integration layer | Does not exist |
| Monitoring agent | Does not exist |
| Real-time (SignalR) | Does not exist |
| React dashboard | Does not exist |
| OpenTelemetry / traces | Correlation ids only |

## 8. Conclusion

Roughly **35–40%** of the existing backend (host, identity, authorization,
audit, feature plumbing, test harness, infra) is reusable for the control
plane. Roughly **50%** is store business logic that must leave the .NET
solution. The frontend, store template, agent, monitoring, billing, and the
entire **feature registry and delivery** subsystem are greenfield — the last of
these is the single largest new area of the project.
=======
Snapshot: **2026-08-24**, commit `3076afb`, 87 commits, 748 tracked files.

This document answers one question — *what is actually in this repository
today* — so that nothing else has to be trusted on the subject. When it and any
other document disagree about what exists, this one is wrong and should be
fixed, not worked around.

> **History note.** The first version of this file described the repository as
> it was before the pivot: a single shared multi-tenant food-service SaaS whose
> business modules lived inside the control plane, with an empty `frontend/`.
> All of that is gone. Phase 8 ported the business domains to Django and deleted
> their .NET counterparts, and an architecture test now fails the build if one
> reappears. What follows describes the repository as it is.

## 1. Summary

KNIGHT is a control plane and nothing else. It manages independent Django
stores; it does not contain them.

| | |
|---|---|
| **Control plane** | .NET 10 modular monolith, 12 modules, 195 C# files across `src/`, 148 in `modules/` |
| **Dashboard** | React 19 + Vite + TypeScript, 19 screen folders, RTL-first |
| **Stores** | One reference Django store with the full integration layer and the ported business domains |
| **Features** | Four installable Django packages and the tool that builds, signs and publishes them |
| **Agent** | 541 lines of dependency-free Python |
| **Deployment** | `install.sh` and `knightctl.sh`; no container images yet |
| **Tests** | 735 backend, 9 dashboard, 156 store — all green |
| **ADRs** | 31 |

## 2. Backend (`backend/`)

`Knight.slnx`, .NET 10, nullable enabled, architecture tests enforcing the
dependency rules.

### 2.1 Core projects (`backend/src`)

| Project | Files | What it holds |
|---|---|---|
| `Knight.Api` | 53 | The host: 20 endpoint files, 8 background services, middleware, auth policies, rate-limit partitions, SignalR hub, composition |
| `Knight.Application` | 30 | Abstractions and authorization primitives. No module may depend on another through it |
| `Knight.Contracts` | 20 | Request and response DTOs. No entity is ever returned from an endpoint |
| `Knight.Domain` | 10 | Shared domain primitives, exceptions, versioning |
| `Knight.Infrastructure` | 82 | EF Core persistence, 29 control-plane migrations, repositories, caching, replay guards, artifact signing, telemetry, health checks |

### 2.2 Modules (`backend/modules`)

Twelve, each one a boundary an architecture test enforces: no module references
a sibling, the API, or Infrastructure.

| Module | Files | What it owns |
|---|---|---|
| `AccessControl` | 25 | Control-plane accounts, sessions with rotation and reuse detection, roles, permissions, MFA, audit |
| `Customers` | 8 | The customer aggregate and its lifecycle |
| `Stores` | 16 | Stores, slug and domain normalisation, credentials with rotation, integration status |
| `Plans` | 8 | Plans, plan features with pinned version ranges, time-boxed prices |
| `Subscriptions` | 9 | The subscription state machine, entitlement records and reconciliation |
| `Billing` | 9 | Billing accounts, invoices with gapless numbering, payment records, the billing run |
| `FeatureRegistry` | 18 | Feature identity, versions, manifests, artifacts, publication and yanking |
| `FeatureDelivery` | 13 | Installations, typed jobs, dependency resolution, staged rollouts |
| `Ingestion` | 8 | Batch caps, idempotency, the limits one store may not exceed |
| `Servers` | 13 | Servers, agents, metrics |
| `Observability` | 14 | Error grouping, incidents, alerts, notification channels |
| `Provisioning` | 7 | The provisioning job and its manual steps |

### 2.3 Tests

| Project | Files | Tests |
|---|---|---|
| `Knight.UnitTests` | 32 | 581 |
| `Knight.IntegrationTests` | 14 | 141, PostgreSQL-backed |
| `Knight.ArchitectureTests` | 2 | 13 |

Release-blocking by convention: the customer-isolation suite, the entitlement
suite, the delivery-security suite and the staged-rollout suite. `REQUIRE_POSTGRES_TESTS=1`
turns a skipped PostgreSQL suite into a failure, and CI sets it.

### 2.4 Tools

`Knight.Bootstrap` applies migrations and seed data (`--migrate-only`, what a
deploy runs) and creates the first administrator. `Knight.LoadTest` is what
measured ingestion at 1,882 req/s before anyone was allowed to propose a broker.

## 3. Dashboard (`frontend/knight-dashboard`)

React 19, Vite, TypeScript, Tailwind, TanStack Query, Zustand, SignalR,
i18next. RTL-first with Persian as the default locale. 19 feature folders:
access, alerts, audit, auth, billing, customers, dashboard, errors, features,
incidents, infrastructure, installations, logs, monitoring, plans, reports,
settings, shared, stores.

Every screen runs against the real API. `VITE_USE_MOCKS=true` serves built-in
fixtures instead, which is a development convenience and not a deployment mode.

`VITE_API_BASE_URL` and `VITE_SIGNALR_URL` are optional: unset, the bundle
addresses `/api/v1` and `/hubs/control-plane` relatively, so it carries no
hostname and no scheme.

Nine vitest cases cover the screens. There is no Playwright suite; the browser
walk each phase ends with is still driven by hand.

## 4. Stores (`stores/reference-store`)

An independent Django application with its own database, which KNIGHT manages
and never connects to.

- `apps/` — the ported business domains: `catalog`, `orders`, `payments`,
  `fulfillment`, `shoppers`, `shop`
- `knight_integration/` — 40 Python files: configuration, HTTP client,
  credential and token handling, the entitlement cache, error middleware, event
  reporting, the health surface KNIGHT polls, the installer that applies
  delivery jobs, and four management commands

The layering rule is a test, not a convention: `test_boundaries.py` fails if a
business module reaches past the feature façade, or if the integration layer
imports a business model. 156 tests across 11 files.

Both sides validate against `docs/contracts/store-integration.schema.json` and
the worked examples beside it, so a contract change that breaks either fails on
both.

## 5. Features (`features/`)

Four packages — `analytics-core`, `analytics-reports`, `promotions`,
`delivery` — each with a `knight_manifest.yaml`, and `tools/knight_package.py`,
which builds a deterministic zip, computes the digest from the built file rather
than taking the author's word for it, signs it, uploads it and registers the
version.

Signing keys are ECDSA P-256. The private half never appears on a KNIGHT server:
`keygen` exists for development and says so.

## 6. Agent (`agent/`)

541 lines of Python with no third-party dependencies. It reaches out and listens
on nothing, its vocabulary is closed — it applies queued installation jobs and
takes no command, path or script — and its credential is revocable and
machine-bound.

## 7. Deployment (`install.sh`, `knightctl.sh`, `infrastructure/`)

- `install.sh` — a fresh Ubuntu or Debian server to a working KNIGHT in one
  command ([`installation.md`](installation.md))
- `knightctl.sh` — status, checks, logs, update, backup, restore,
  administrators, domain, signing key, uninstall
- `infrastructure/scripts/` — backup, restore, and the restore drill CI runs on
  every push
- `infrastructure/docker/` — PostgreSQL and Redis for local development only

**Not here:** container images and the deploy stages of
[`deployment.md`](deployment.md) §8, which wait on a hosting-platform decision;
an offsite copy of the nightly dumps; and installers for the machines that host
stores.

## 8. CI (`.github/workflows/`)

`backend.yml` — secret scan over full history, build and test with
`REQUIRE_POSTGRES_TESTS=1`, `dotnet format --verify-no-changes`, NuGet and npm
dependency audits, shellcheck, Feature packaging with manifest validation, and
the migration and restore drill (migrations applied twice to prove idempotence,
a real dump restored and compared, and a deliberately corrupted dump that the
restore must refuse).

`store.yml` — the reference store against PostgreSQL with the optional Features
installed and registered, and the dashboard: type check, tests, build.

## 9. What is not built

| | |
|---|---|
| **Blocked on someone outside the project** | The external security review of the code-delivery path ([`security/external-review-scope.md`](security/external-review-scope.md)). R16 stays open until it has happened |
| **Blocked on a decision** | Container images and deploy stages, pending a hosting platform |
| **Open work** | Log search and export; a Playwright suite; type generation from OpenAPI; per-route error boundaries; a job-progress component; DNS TXT domain verification; signed agent releases; manual merge and split of error groups; time partitioning for `server_metrics`; tax computation |

The full list, phase by phase, is [`../TODO.md`](../TODO.md).
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
