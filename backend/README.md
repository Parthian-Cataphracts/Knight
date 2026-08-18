# Platform Backend

.NET 10 / ASP.NET Core solution implementing the shared platform API.

See [`docs/architecture/repository-structure.md`](../docs/architecture/repository-structure.md)
for the full project layout and dependency rules.

## Solution layout

```
src/
  Knight.Api/              composition root, HTTP endpoints, middleware
  Knight.Application/      application abstractions, authorization primitives
  Knight.Domain/           entities, value objects, domain exceptions
  Knight.Infrastructure/   EF Core, Redis, JWT, password hashing, object storage
  Knight.Contracts/        API request/response DTOs

modules/
  Identity/           platform admins, tenant users, roles, refresh tokens, authentication
  Tenancy/            tenants, tenant resolution, tenant context
  FeatureManagement/  feature definitions, tenant feature toggles

tools/
  Knight.Bootstrap/  manual, offline console tool to provision the first PlatformAdmin

tests/
  Knight.UnitTests/
  Knight.IntegrationTests/
  Knight.ArchitectureTests/
```

## First platform admin

There is no public registration endpoint. Provision the first `PlatformAdmin`
by running the bootstrap tool against the target database, once, by hand:

```bash
PLATFORM_DB_CONNECTION_STRING="Host=localhost;Port=5432;Database=platform;Username=platform;Password=platform" \
  dotnet run --project tools/Knight.Bootstrap -- --email admin@example.com
```

It prompts for the password interactively (masked, confirmed) and is safe to
re-run against an already-provisioned email. See
`docs/architecture/authorization.md`.

## Commands

```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Knight.Api
```

## Testing

`Knight.UnitTests` and `Knight.ArchitectureTests` are self-contained.

`Knight.IntegrationTests` includes a PostgreSQL-backed security/isolation
suite (`Security/*`) that spins up an ephemeral `postgres:17-alpine`
container per run via [Testcontainers](https://dotnet.testcontainers.org/) —
**this requires a running Docker (or Docker-API-compatible) daemon**. If none
is reachable, those tests detect it and skip themselves rather than failing
the run — see `docs/adr/0005-postgresql-integration-testing.md`. No manual
database setup is required; the fixture provisions and migrates its own
database per test run.

## Migrations

```bash
dotnet ef migrations add <Name> \
  --project src/Knight.Infrastructure \
  --startup-project src/Knight.Api \
  --output-dir Persistence/Migrations
```

Requires the `dotnet-ef` tool (`dotnet tool install --global dotnet-ef`),
matching the solution's EF Core version. Migrations are never applied
automatically against any database by tooling in this repository — running
`dotnet ef database update` (or an equivalent explicit step) against a real
environment is a deliberate, separate action.
