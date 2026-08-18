# 0005. PostgreSQL Integration Testing via Testcontainers

## Status

Accepted

## Context

Tenant isolation is enforced by an EF Core global query filter running against
PostgreSQL-specific features (partial unique indexes, `Npgsql` exception
types). Mocking `DbContext` or substituting the EF Core InMemory provider
would not exercise the actual filter/index/constraint behavior the security
model depends on, and would not have caught the class of defect this phase
was specifically asked to review (query filters that behave correctly in
principle but subtly misbind under real provider/model-caching behavior).
Requiring every contributor to stand up and maintain a permanent local
PostgreSQL instance for tests is also unreliable and drifts from what CI runs.

## Decision

Integration tests that need real PostgreSQL behavior (tenant isolation,
resolution, unique-constraint conflicts) spin up an ephemeral
`postgres:17-alpine` container per test collection via Testcontainers
(`Testcontainers.PostgreSql`), migrate it with the real `PlatformDbContext`
migrations, and boot the full API host (`WebApplicationFactory<Program>`)
against it. See `PostgresApiFixture` in `Knight.IntegrationTests`.

These tests require a reachable Docker (or Docker-API-compatible) daemon.
When one is not available, the fixture catches the startup failure and marks
itself unavailable; dependent tests detect this and return early rather than
failing the whole suite, so `dotnet test` still succeeds in environments
without container support — but the tests provide no coverage there. Local
development and CI are expected to have Docker available so this suite runs
for real; see `backend/README.md`.

Setting the `REQUIRE_POSTGRES_TESTS=1` environment variable switches this to
**mandatory mode**: a container start failure is rethrown instead of
swallowed, failing the run loudly instead of silently reporting zero
PostgreSQL coverage as a pass. Use this for CI and for explicit security
validation passes — a validation run must never be able to report success
while this suite was actually skipped.

This suite was validated for real (Phase 01.1): with a reachable Docker
daemon and `REQUIRE_POSTGRES_TESTS=1`, all 30 PostgreSQL-backed integration
tests passed with 0 skipped, and `InitialCreate` was applied cleanly to a
freshly created, empty database. That run also surfaced one genuine defect
(see `TenantRepository.RegisterNewDomainAsync` and the note below), which
mocks or the InMemory provider would not have caught — reinforcing the
rationale above.

## Consequences

- Tenant isolation and resolution behavior is proven against the actual
  database engine and constraints used in production, not an approximation.
- No developer needs to provision or maintain a standing test database;
  `dotnet test` is self-contained wherever Docker is present.
- The test suite has a real (Docker) dependency, which is heavier than pure
  unit tests and requires Docker in CI for this coverage to actually run.
- Environments without Docker (e.g. some sandboxes) silently skip this
  coverage rather than failing — a deliberate tradeoff to keep the overall
  test run reliable, documented here so it is not mistaken for the tests
  having been removed or never written.
