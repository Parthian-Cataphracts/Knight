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
| Put it on a server | [`docs/installation.md`](docs/installation.md) |
| Start developing | [`docs/development.md`](docs/development.md) |
| See KNIGHT, a store and the dashboard working together | [`docs/phase-3-verification.md`](docs/phase-3-verification.md) |

> **Context for anyone picking this up:** the pivot to the control-plane
> architecture above ([`docs/adr/0010`](docs/adr/0010-pivot-to-control-plane.md))
> has landed. The business modules of the previous product — a shared
> multi-tenant food-service SaaS — were ported to Django and deleted from this
> solution in phase 8, and an architecture test fails the build if one
> reappears. Read `docs/README.md` before trusting any other document.

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
| Local infra | PostgreSQL; Redis optional in development, required elsewhere |

## Repository layout

```
knight/
├── backend/          .NET solution — the control plane, and nothing else
├── frontend/         React + Vite dashboard
├── stores/           Django reference store + the knight_integration layer
├── features/         Feature packages, one per capability
├── agent/            the daemon that runs on a managed server
├── infrastructure/   compose file, backup/restore scripts, deployment notes
├── docs/             architecture, contracts, ADRs, security, risks
├── install.sh        one-command server install
├── knightctl.sh      management tool for an installed deployment
└── TODO.md           phase-by-phase status
```

## Install it on a server

Ubuntu 22.04+ or Debian 12+, as root, with the domain's DNS A record already
pointing at the machine:

```bash
bash <(curl -Ls https://raw.githubusercontent.com/Parthian-Cataphracts/Knight/main/install.sh)
```

Everything KNIGHT owns lives under `/opt/knight` and runs as an unprivileged
`knight` user: its own Redis instance, its own database, free ports chosen at
install, one nginx site, and a private .NET and Node wherever the host's are too
old to build with. A server already running something else keeps running it.
`knightctl` manages the result. [`docs/installation.md`](docs/installation.md)
says exactly what is created and what is not.

## Quick start (development)

Needs .NET 10, Node 20+, Python 3.12+ and a PostgreSQL. Docker is not required
for anything, including the integration suite;
[`docs/development.md`](docs/development.md) §2 starts a PostgreSQL from the
binaries-only distribution if you have none.

```bash
cd backend && dotnet restore && dotnet build && dotnet test
dotnet run --project src/Knight.Api --urls http://localhost:5008
```

```bash
cd frontend/knight-dashboard && npm install
cp .env.example .env.local          # then set VITE_USE_MOCKS=false
npm run dev
```

Migrations, the first administrator, and the reference store are in
[`docs/development.md`](docs/development.md).
[`docs/phase-3-verification.md`](docs/phase-3-verification.md) walks the whole
thing end to end: register a store, prove its domain, watch it report.

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
