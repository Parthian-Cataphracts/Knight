# Development Guide

Status: **authoritative**.

## 1. Prerequisites

- .NET 10 SDK
- Node.js 20+ and pnpm (dashboard, once it exists)
- Python 3.12+ (reference Django store, once it exists)
- Docker Desktop (PostgreSQL, Redis, integration tests)

## 2. Running today

```bash
cd infrastructure/docker && docker compose up -d
```

```bash
cd backend && dotnet restore && dotnet build
```

```bash
dotnet run --project backend/src/Knight.Api
```

Development-only: OpenAPI at `/openapi/v1.json`, API reference at `/scalar`,
health at `/health/live` and `/health/ready`.

```bash
cd backend && dotnet test
```

The PostgreSQL-backed security suite in `Knight.IntegrationTests` needs a
running Docker daemon.

> The dashboard and the reference store do not exist yet. Their commands will
> be added here when Phase 7 and Phase 4 begin.

## 3. Working agreements

- **Read `docs/README.md` first.** It says which documents are authoritative.
- Work phase by phase, following `../TODO.md`. Do not start a phase before the
  previous phase's exit criteria are met.
- Every meaningful change follows the loop:
  `analyse → design → implement → test → inspect → fix → refactor → document → update TODO → report`.
- A feature is done only with: implementation, validation, tests,
  authorization, error handling, documentation, observability, TODO update.
- Record any decision with long-term impact as an ADR in `docs/adr/`.
- Never claim completion without running the tests and reporting the output.

## 4. Code conventions

**Backend** — nullable enabled, `TreatWarningsAsErrors` where practical, one
public type per file, domain invariants inside aggregates (not services), DTOs
in `Knight.Contracts`, no entity ever returned from an endpoint, async all the
way with `CancellationToken` propagated, UTC everywhere.

**Frontend** — strict TypeScript, feature-folder structure, no `fetch` outside
`lib/api`, server state in TanStack Query, logical CSS properties only (RTL),
mobile-first, every screen verified in both directions.

**Django store** — business apps never import `knight_integration`; the
integration layer never imports business models.

**Feature packages** — one Django app per feature under its own app label; a
valid `knight_manifest.yaml`; migrations only for its own models;
expand/contract for destructive change; no customer-specific code or config; a
health check function; tests that run against the reference store. A feature is
implemented **once** and never copied into a store by hand.

## 5. Testing expectations

| Layer | Required |
|---|---|
| Domain | Unit tests for every invariant and state transition |
| Application | Unit tests for business rules, entitlement, pricing |
| API | Integration tests for happy path, validation, authorization |
| Security | Isolation tests per resource type (release-blocking, `authorization.md` §6) |
| Architecture | Dependency rules enforced by `Knight.ArchitectureTests` |
| Frontend | Component tests for stateful components; Playwright for login, store registration, incident triage |
| Integration | KNIGHT ↔ reference store, KNIGHT ↔ agent |
| Feature delivery | Install/upgrade/rollback/uninstall of the reference feature against a real store + DB; dependency and compatibility resolution; signature/digest rejection; job idempotency and retry |

## 6. Git conventions

- Branch per unit of work: `feat/…`, `fix/…`, `docs/…`, `chore/…`.
- Conventional commit subjects.
- A PR that changes architecture updates the relevant doc and `TODO.md` in the
  same PR. Reviews check the doc, not only the code.

## 7. Definition of ready for a new agent/session

Before writing code, confirm you can answer:

1. Which phase is active in `TODO.md`?
2. Which document defines the contract you are implementing?
3. Which permission and which entitlement guard this operation — and, for a
   store capability, is it entitlement, installation, or both?
4. How is customer isolation enforced for the data you touch?
5. What test proves it?
