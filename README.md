# KNIGHT

**Central control plane for independent Django-based e-commerce stores.**

A web-design company builds an online store for each of its customers. Every
store is an independent application — its own domain, its own Django backend,
its own database, its own deployment. KNIGHT is the platform that manages,
bills, configures, and observes all of them from one place.

```
                         KNIGHT
                 ASP.NET Core + React dashboard
                 PostgreSQL · Redis
                            │
        ┌───────────────────┼───────────────────┐
        ▼                   ▼                   ▼
    Store A             Store B             Store C
    Django              Django              Django
    cafe1.ir            cafe2.ir            cafe3.ir
    Server A            Server A            Dedicated server
```

**KNIGHT manages the stores. KNIGHT does not become the stores.**

A capability (Advanced Analytics 1.4.0) is built **once** as a versioned Django
package, registered in KNIGHT, and installed automatically into every store
whose customer bought it:

```
IMPLEMENT ONCE → PACKAGE → REGISTER → PURCHASE → ENTITLEMENT
  → AUTOMATED INSTALL → MIGRATE → CONFIGURE → ENABLE → HEALTH CHECK → MONITOR
```

No feature is ever re-implemented or hand-copied per customer.

## Start here

| If you want to… | Read |
|---|---|
| Understand the project and which docs are current | [`docs/README.md`](docs/README.md) |
| Know what is built and what remains | [`TODO.md`](TODO.md) |
| Know what is actually in this repo today | [`docs/current-state-analysis.md`](docs/current-state-analysis.md) |
| Understand the target architecture | [`docs/architecture.md`](docs/architecture.md) |
| Understand how Features are built and delivered | [`docs/feature-delivery.md`](docs/feature-delivery.md) |
| Start developing | [`docs/development.md`](docs/development.md) |

> **Important context for anyone (human or agent) picking this up:** the code
> currently in `backend/` implements a *previous* product — a shared
> multi-tenant food-service SaaS. The project has pivoted to the control-plane
> architecture above ([`docs/adr/0010`](docs/adr/0010-pivot-to-control-plane.md)),
> and the pivot is in progress. Read `docs/README.md` before trusting any other
> document.

## Scope

**KNIGHT owns:** customers, stores, plans, the **Feature registry and delivery
pipeline**, subscriptions and entitlements, billing, users/roles/permissions,
servers and infrastructure metadata, store provisioning, monitoring, errors and
incidents, logs, notifications, audit, reports.

**KNIGHT never owns:** products, orders, payments, inventory, menus, or any
other store business logic. That lives in each store's own Django application.

## Technology

| Layer | Choice |
|---|---|
| Control-plane API | .NET 10 / ASP.NET Core (modular monolith) |
| Control-plane data | PostgreSQL (EF Core) + Redis |
| Dashboard | React 19 + Vite + TypeScript, RTL-first, responsive |
| Real-time | SignalR |
| Stores | Django + DRF, one database each |
| Features | Signed, versioned Django packages in a private registry |
| Telemetry + delivery | KNIGHT Agent (outbound only; typed lifecycle jobs) |
| Local infra | Docker Compose |

## Repository layout

```
knight/
├── backend/          .NET solution (control plane; store modules frozen pending port)
├── frontend/         React + Vite dashboard (to be created — Phase 6)
├── stores/           Django reference store + integration layer (to be created — Phase 3)
├── features/         Feature packages, one per capability (to be created — Phase 3.5)
├── infrastructure/   Docker Compose, database/storage/reverse-proxy notes
├── docs/             architecture, contracts, ADRs, security, risks
└── TODO.md           phase-by-phase status
```

## Quick start

```bash
cd infrastructure/docker && docker compose up -d
```

```bash
cd backend && dotnet restore && dotnet build && dotnet test
```

```bash
dotnet run --project backend/src/Knight.Api
```

Development only: OpenAPI at `/openapi/v1.json`, API reference at `/scalar`,
health at `/health/live` and `/health/ready`.

## Non-negotiable rules

1. KNIGHT is never the business backend of a store.
2. Stores stay independently deployable.
3. KNIGHT never touches a store's database or depends on its schema.
4. Customer isolation is enforced server-side.
5. The frontend is never the source of truth for authorization.
6. Feature entitlements are enforced by backend systems.
7. No secrets in source control, logs, or API responses.
8. No microservice, broker, or orchestrator without a recorded justification —
   a Feature Package is a Django app, not a service.
9. A Feature is implemented once and delivered automatically, never copied per
   customer.
10. Entitlement is not installation; both are tracked, neither is one boolean.
11. Only signed, digest-verified artifacts are installed, through a fixed typed
    job vocabulary.

## Configuration and secrets

Standard .NET configuration (`appsettings.json`, environment overrides,
environment variables). No real secrets are committed;
`appsettings.Development.json` holds placeholders only. See
[`docs/deployment.md`](docs/deployment.md) and
[`docs/security-threat-model.md`](docs/security-threat-model.md).
