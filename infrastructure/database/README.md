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

## Connection pooling (PgBouncer)

Every store has its own database and its own connections. At scale it is the
*count* of those independent connections, not query load, that PostgreSQL hits
first — so the compose file runs **PgBouncer** in front of PostgreSQL, on port
`6432` (override with `PGBOUNCER_PORT`), in **transaction** pooling mode, which is
the mode that actually multiplexes many client connections onto a small pool of
server ones (hardening backlog P1).

To use it, point the control plane at PgBouncer instead of PostgreSQL:

```
CONTROL_PLANE_DB_CONNECTION_STRING="Host=localhost;Port=6432;Database=knight;Username=knight;Password=knight"
```

Connecting straight to PostgreSQL on `5432` still works and is what most local
development does; the pool matters when there are many stores, not one developer.

**No application change is needed.** Transaction pooling forbids session state that
outlives a transaction — most importantly *server-side* prepared statements — and
EF Core with Npgsql does not use them by default (Npgsql only prepares when
`Max Auto Prepare` is set, which KNIGHT does not). This was verified by running
the API against `Port=6432` and reading through it; if you ever enable Npgsql
auto-prepare, keep PgBouncer in `session` mode instead.

The pool sizes (`DEFAULT_POOL_SIZE`, `MAX_CLIENT_CONN`) are set in the compose
file. PgBouncer's own stats are reachable as the `pgbouncer` admin database
(`psql -p 6432 pgbouncer` → `SHOW POOLS;`).

A production deployment runs PgBouncer as a system service, not a container, and
with scram end to end via an `auth_query` rather than the plaintext userlist the
development image derives from the compose environment.
