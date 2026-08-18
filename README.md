# Knight

A centralized, multi-tenant platform for food service businesses (restaurants,
cafés, dessert shops, bakeries, and similar establishments), each with an
independently designed storefront, admin interface, and domain, backed by a
shared ASP.NET Core API.

## Architecture

- **Style:** modular monolith (see `docs/adr/0001-modular-monolith.md`)
- **Backend:** .NET 10, ASP.NET Core, C#
- **Database:** PostgreSQL via Entity Framework Core, centralized with
  tenant-scoped data (`docs/adr/0002-central-postgresql-with-tenant-scoping.md`)
- **Cache:** Redis
- **Object storage:** abstracted, S3-compatible in production
- **Frontends:** independently deployed Next.js/TypeScript applications per
  tenant, under `frontend/`

Full details live in `docs/architecture/`:

- [`platform-overview.md`](docs/architecture/platform-overview.md)
- [`multi-tenancy.md`](docs/architecture/multi-tenancy.md)
- [`authorization.md`](docs/architecture/authorization.md)
- [`repository-structure.md`](docs/architecture/repository-structure.md)

## Repository layout

```
Knight/
├── backend/                .NET solution — see backend/README.md
├── frontend/               Next.js/TypeScript applications — see frontend/README.md
│   ├── super-admin/        reserved for the future Super Admin frontend
│   ├── tenants/            reserved; empty until a tenant is onboarded
│   └── shared/             code shared across multiple tenant frontends
├── infrastructure/         Docker Compose, database, storage, reverse-proxy notes
└── docs/                   architecture docs, ADRs, API/database/security notes
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Docker (for local PostgreSQL and Redis)

## Local development

Start local infrastructure:

```bash
cd infrastructure/docker
docker compose up -d
```

Restore and build the backend:

```bash
cd backend
dotnet restore
dotnet build
```

Run the API:

```bash
dotnet run --project src/Knight.Api
```

In the Development environment, the OpenAPI document is available at
`/openapi/v1.json` and an interactive API reference is served at `/scalar`.
Liveness and readiness checks are available at `/health/live` and
`/health/ready`.

## Testing

```bash
cd backend
dotnet test
```

Test projects:

- `tests/Knight.UnitTests` — domain and application behavior
- `tests/Knight.IntegrationTests` — HTTP host, tenant isolation, and
  authorization behavior; the PostgreSQL-backed security suite requires a
  running Docker daemon (see `backend/README.md`)
- `tests/Knight.ArchitectureTests` — enforces the dependency rules in
  `docs/architecture/repository-structure.md`

## Configuration and secrets

Configuration follows standard .NET conventions (`appsettings.json`,
`appsettings.{Environment}.json`, environment variables). No real secrets are
committed to this repository — `appsettings.Development.json` contains only
placeholder values intended for local development against the Docker Compose
infrastructure above. Production connection strings, the JWT signing key, and
any other secret must be supplied via environment variables or a secret store.

See [`docs/security/README.md`](docs/security/README.md) for more detail.
