# Database

PostgreSQL is the platform's system of record. Local development uses the
Postgres service defined in `infrastructure/docker/docker-compose.yml`.

## Connection string

Supplied via configuration (`ConnectionStrings:Platform`) or the
`PLATFORM_DB_CONNECTION_STRING` environment variable used by EF Core design-time
tooling. Never commit a connection string containing real credentials.

## Migrations

See `docs/architecture/repository-structure.md` for where migrations live and
how they are generated and applied.
