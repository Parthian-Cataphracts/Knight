# Current State Analysis

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
