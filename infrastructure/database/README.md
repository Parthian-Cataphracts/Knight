# Database

PostgreSQL is the control plane's system of record. Local development uses the
Postgres service defined in `infrastructure/docker/docker-compose.yml`.

Note that this database holds the control plane and nothing else. Each store
runs its own Django application against its own database, which KNIGHT never
connects to — see `docs/adr/0023-single-tenant-store.md`.

## Connection string

Supplied via configuration (`ConnectionStrings:ControlPlane`) or the
`CONTROL_PLANE_DB_CONNECTION_STRING` environment variable used by EF Core
design-time tooling. Never commit a connection string containing real
credentials.

## Migrations

See `docs/architecture/repository-structure.md` for where migrations live and
how they are generated and applied.
