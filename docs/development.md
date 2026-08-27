# Development Guide

Status: **authoritative**.

## 1. Prerequisites

- .NET 10 SDK
- Node.js 20+ (dashboard)
- Python 3.12+ (reference Django store)
- PostgreSQL 16+ — reachable, not necessarily installed as a service (§2)
- Redis is **optional** in development
  ([`adr/0020`](adr/0020-store-ingestion-authentication.md)); required in every
  other environment, where the host refuses to start without it

Docker is not required for anything, including the integration suite.

## 2. PostgreSQL without a container runtime

Any PostgreSQL the tooling can reach will do — a service, a container, a managed
instance. Where none is available, the binaries-only distribution runs a cluster
out of a directory with no installer and no admin rights:

```bash
# Once. Anywhere outside the repository.
initdb -D ~/knight-dev/pgdata -U knight --pwfile=<(echo knight) -E UTF8 --locale=C
pg_ctl -D ~/knight-dev/pgdata -o "-p 5433" -l ~/knight-dev/pg.log start

createdb -h 127.0.0.1 -p 5433 -U knight knight     # control plane + legacy schema
createdb -h 127.0.0.1 -p 5433 -U knight refstore   # the reference store's own database
```

Port 5433 keeps it clear of anything already listening on 5432. The connection
strings in `appsettings.Development.json` and `stores/reference-store/.env.example`
point at it.

## 3. Running it

```bash
cd backend && dotnet restore && dotnet build
```

Bring both schemas up. The API host deliberately does not migrate itself — that
is a deployment step
([`adr/0018`](adr/0018-separate-control-plane-context-and-access-module.md)):

```bash
export CONTROL_PLANE_DB_CONNECTION_STRING="Host=127.0.0.1;Port=5433;Database=knight;Username=knight;Password=knight"
export ConnectionStrings__ControlPlane="$CONTROL_PLANE_DB_CONNECTION_STRING"

dotnet ef database update --project src/Knight.Infrastructure --startup-project src/Knight.Api --context ControlPlaneDbContext
```

One context, not two. `PlatformDbContext` was the pre-pivot product's and is
gone; the line that migrated it here answered "No DbContext named
'PlatformDbContext' was found" for anybody following these steps.

Create the first administrator. The password is typed in, never passed as an
argument or read from configuration:

```bash
dotnet run --project backend/tools/Knight.Bootstrap -- --control-plane --email admin@example.com
```

That account holds `SuperAdmin`, which requires a second factor, so its first
sign-in returns `mfa_enrollment_required` and can reach nothing but
`POST /api/v1/auth/mfa/enroll` and `/confirm` until MFA is enrolled
([`authentication.md`](authentication.md) §1).

```bash
dotnet run --project backend/src/Knight.Api --urls http://localhost:5008
```

Development-only: OpenAPI at `/openapi/v1.json`, API reference at `/scalar`,
health at `/health/live` and `/health/ready`.

The dashboard, against the real API:

```bash
cd frontend/knight-dashboard && npm install
cp .env.example .env.local          # then set VITE_USE_MOCKS=false
npm run dev
```

The reference store: see
[`stores/reference-store/README.md`](../stores/reference-store/README.md).

## 4. Testing

```bash
cd backend && dotnet test
```

The PostgreSQL-backed suites need a database. Point `KNIGHT_TEST_POSTGRES` at a
server they may create databases on — each run creates its own and drops it
afterwards — or leave it unset and let Testcontainers start one, which needs a
Docker-compatible daemon:

```bash
KNIGHT_TEST_POSTGRES="Host=127.0.0.1;Port=5433;Database=postgres;Username=knight;Password=knight" \
REQUIRE_POSTGRES_TESTS=1 dotnet test
```

`REQUIRE_POSTGRES_TESTS=1` turns a skipped PostgreSQL suite into a failure. CI
sets it, and so should any run whose result is going to be believed
([`adr/0005`](adr/0005-postgresql-integration-testing.md)).

The store side:

```bash
cd stores/reference-store && .venv/Scripts/python manage.py test knight_integration
```

Both sides validate against `docs/contracts/store-integration.schema.json` and
the worked examples beside it, so a contract change that breaks either fails on
both.

## 5. Working agreements

- **Read `docs/README.md` first.** It says which documents are authoritative.
- Work phase by phase, following `../TODO.md`. Do not start a phase before the
  previous phase's exit criteria are met.
- Every meaningful change follows the loop:
  `analyse → design → implement → test → inspect → fix → refactor → document → update TODO → report`.
- A feature is done only with: implementation, validation, tests,
  authorization, error handling, documentation, observability, TODO update.
- Record any decision with long-term impact as an ADR in `docs/adr/`.
- Never claim completion without running the tests and reporting the output.

## 6. Code conventions

**Backend** — nullable enabled, `TreatWarningsAsErrors` where practical, one
public type per file, domain invariants inside aggregates (not services), DTOs
in `Knight.Contracts`, no entity ever returned from an endpoint, async all the
way with `CancellationToken` propagated, UTC everywhere.

**Frontend** — strict TypeScript, feature-folder structure, no `fetch` outside
`lib/api`, server state in TanStack Query, logical CSS properties only (RTL),
mobile-first, every screen verified in both directions.

**Django store** — business apps never import `knight_integration` beyond the
feature façade; the integration layer never imports business models. Both halves
are enforced by `knight_integration/tests/test_boundaries.py`.

**Feature packages** — one Django app per feature under its own app label; a
valid `knight_manifest.yaml`; migrations only for its own models;
expand/contract for destructive change; no customer-specific code or config; a
health check function; tests that run against the reference store. A feature is
implemented **once** and never copied into a store by hand.

## 7. Testing expectations

| Layer | Required |
|---|---|
| Domain | Unit tests for every invariant and state transition |
| Application | Unit tests for business rules, entitlement, pricing |
| API | Integration tests for happy path, validation, authorization |
| Security | Isolation tests per resource type (release-blocking, `authorization.md` §6) |
| Architecture | Dependency rules enforced by `Knight.ArchitectureTests` |
| Frontend | Component tests for stateful components; Playwright for login, store registration, incident triage |
| Integration | KNIGHT ↔ reference store, both sides against the shared contract |
| Feature delivery | Install/upgrade/rollback/uninstall of the reference feature against a real store + DB; dependency and compatibility resolution; signature/digest rejection; job idempotency and retry |

## 8. Git conventions

- Branch per unit of work: `feat/…`, `fix/…`, `docs/…`, `chore/…`.
- Conventional commit subjects.
- A PR that changes architecture updates the relevant doc and `TODO.md` in the
  same PR. Reviews check the doc, not only the code.

## 9. Definition of ready for a new agent/session

Before writing code, confirm you can answer:

1. Which phase is active in `TODO.md`?
2. Which document defines the contract you are implementing?
3. Which permission and which entitlement guard this operation — and, for a
   store capability, is it entitlement, installation, or both?
4. How is customer isolation enforced for the data you touch?
5. What test proves it?
