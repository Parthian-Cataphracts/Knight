> **LEGACY DOCUMENT.** This describes the previous product (a shared
> multi-tenant food-service SaaS), not KNIGHT's target control-plane
> architecture. See [`docs/README.md`](../README.md) and
> [`docs/adr/0010`](../adr/0010-pivot-to-control-plane.md). Kept because it
> documents code that still exists in `backend/`.

# Repository Structure

```
Knight/
|
+-- backend/                .NET solution (see below)
|
+-- frontend/
|   +-- super-admin/        reserved for the future Super Admin frontend
|   +-- tenants/            reserved; empty until a tenant is onboarded
|   +-- shared/             code shared across multiple tenant frontends (empty until needed)
|
+-- infrastructure/
|   +-- docker/             local Postgres/Redis via docker-compose
|   +-- database/
|   +-- reverse-proxy/
|   +-- storage/
|   +-- scripts/
|
+-- docs/
    +-- architecture/
    +-- adr/
    +-- api/
    +-- database/
    +-- security/
```

## Backend solution

```
backend/
|
+-- src/
|   +-- Knight.Api/              composition root, HTTP endpoints, middleware
|   +-- Knight.Application/      use-case-facing abstractions, authorization primitives
|   +-- Knight.Domain/           entities, value objects, domain exceptions (no external deps)
|   +-- Knight.Infrastructure/   EF Core, Redis, JWT, password hashing, object storage
|   +-- Knight.Contracts/        API request/response DTOs
|
+-- modules/
|   +-- Identity/          PlatformAdmin, TenantUser, Role, RefreshToken
|   +-- Tenancy/           Tenant, tenant resolution, tenant context
|   +-- FeatureManagement/ FeatureDefinition, TenantFeature, IFeatureAccessService
|
+-- tests/
|   +-- Knight.UnitTests/
|   +-- Knight.IntegrationTests/
|   +-- Knight.ArchitectureTests/
|
+-- Knight.slnx
```

## Dependency rules

```
Domain
  ^
Application
  ^
Modules (Identity, Tenancy, FeatureManagement)
  ^
Infrastructure
  ^
Api
```

- `Knight.Domain` has no dependency on anything else in the solution and no
  dependency on ASP.NET Core or Entity Framework Core.
- `Knight.Application` depends only on `Knight.Domain`.
- A module (`Identity`, `Tenancy`, `FeatureManagement`) depends on
  `Knight.Domain` and `Knight.Application`, never on `Knight.Infrastructure`,
  `Knight.Api`, or another module.
- `Knight.Infrastructure` implements abstractions declared in `Knight.Application`
  and the modules; it depends on all of them.
- `Knight.Api` composes everything: `Knight.Application`,
  `Knight.Infrastructure`, `Knight.Contracts`, and every module.

These rules are enforced by `Knight.ArchitectureTests` (`LayeringTests.cs`),
not just documented — a build that violates them fails.

## Migrations

EF Core migrations are owned by `Knight.Infrastructure` and generated
against `PlatformDbContext`. `DesignTimeDbContextFactory` lets `dotnet ef`
tooling construct the context without running the full API host:

```bash
dotnet ef migrations add <Name> \
  --project backend/src/Knight.Infrastructure \
  --startup-project backend/src/Knight.Api
```

Migrations are never applied automatically against production data; applying
them is an explicit operational step.

## Adding a module

1. Create the module project under `backend/modules/<Name>` with a
   `Domain/` folder for entities and module-specific repository interfaces.
2. Reference `Knight.Domain` and `Knight.Application` only.
3. If the module owns permissions, implement `IPermissionProvider`.
4. Add an `Add<Name>Module(IServiceCollection)` extension and register it in
   `Knight.Api.Composition.ModuleRegistration`.
5. Implement any persistence contracts (`I...Repository`, `I...Store`) in
   `Knight.Infrastructure`, including EF Core configuration and migrations.
