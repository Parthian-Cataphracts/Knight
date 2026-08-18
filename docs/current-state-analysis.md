# Current State Analysis

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
