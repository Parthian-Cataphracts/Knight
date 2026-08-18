# Observability

Status: **authoritative proposal**.

KNIGHT is an observability product, so it must be observable itself. Three
signals, one correlation model.

## 1. Correlation model

Every record — in KNIGHT, in a store, and in the agent — carries as much of
this context as it has:

```
correlationId / requestId    one logical request
traceId + spanId             W3C traceparent, propagated across HTTP hops
customerId                   who owns it
storeId / serverId           what it concerns
environment                  Development | Staging | Production
service                      knight-api | store-<slug> | knight-agent
version                      application version
```

This is what makes the chain traceable:

```
Customer -> Store -> Server -> Request -> Error -> Incident
```

`X-Correlation-Id` is accepted from clients and always echoed. `traceparent` is
propagated to store calls so a store's own traces can be joined to KNIGHT's.

## 2. Logging

- Structured JSON logs everywhere; no string-concatenated messages.
- Levels: `Trace/Debug` (dev only), `Information` (state changes),
  `Warning` (degradations), `Error` (failed operations), `Critical` (outages).
- Never logged: passwords, hashes, tokens, client secrets, full request bodies,
  personal data beyond identifiers. A central redaction helper is mandatory.
- Ingested store logs are stored with retention and are entitlement-gated
  (`logs.view` + the log-shipping feature).

## 3. Metrics

KNIGHT self-metrics (OpenTelemetry-compatible):

```
http.server.request.duration        by route, status
knight.ingest.events                by type, store, outcome
knight.store.health.check.duration  by store, outcome
knight.agent.heartbeat.age          by server
knight.errors.groups.created        by store
knight.incidents.open               gauge
knight.jobs.queued / running        gauge, by type
knight.jobs.duration                by type, outcome
knight.jobs.failed                  by type, step, errorCode
knight.feature.installations        gauge, by feature, version, state
knight.feature.rollbacks            by outcome (succeeded | manual_required)
db.query.duration, cache.hit.ratio, queue.depth
```

Store and server metrics arrive through ingestion and are stored as
`ServerMetric` / `StoreHealthCheck` rows.

## 4. Tracing

OpenTelemetry SDK with OTLP export, disabled by default in Development. Spans
cover incoming requests, database calls, Redis calls, outbound store calls, and
background jobs. Nothing custom is invented — instrumentation stays standard so
a collector, Jaeger, or a vendor can be attached later without code changes.

## 5. Health

```
/health/live    process is alive, no dependencies touched
/health/ready   database + cache reachable
```

Store health is a *product feature*, not a KNIGHT liveness concern: a store
being down must never make KNIGHT report unready.

## 6. Error aggregation

See [`adr/0013`](adr/0013-error-grouping-strategy.md). Summary:

```
ingest -> normalise -> fingerprint -> upsert ErrorGroup -> append ErrorEvent
       -> evaluate spike rules -> maybe raise Alert / open Incident
```

Raw events are retained shorter than groups; groups keep counts, first/last
seen, and a bounded sample of events.

## 7. Retention (initial policy, configurable)

| Data | Retention |
|---|---|
| `ServerMetric` raw (1 min) | 7 days |
| `ServerMetric` rolled up (1 hour) | 90 days |
| `StoreHealthCheck` | 30 days |
| `ErrorEvent` | 30 days |
| `ErrorGroup` | 1 year |
| `LogEntry` | 14 days (plan-dependent) |
| `AuditLog` | 2 years, never auto-deleted below legal minimum |
| `FeatureInstallationJob` + `JobStepResult` | 1 year (job history is audit-adjacent) |
| `Incident` | indefinite |

Retention is enforced by a scheduled job with partitioned tables for the
high-volume ones.

## 8. Alerting rules (initial)

```
server.offline          no agent heartbeat for 3 intervals
feature.install.failed  an installation or upgrade job failed
feature.entitled_not_installed  entitlement active but installation absent > threshold
feature.drift           store-reported feature set differs from KNIGHT's record
job.stuck               job Running beyond its timeout
store.unreachable       3 consecutive failed health checks
store.degraded          dependency reported unhealthy
error.spike             group occurrences exceed N× the 7-day baseline
error.new_critical      first occurrence of a 5xx group in Production
backup.failed           store reports a failed backup event
```

Alerts feed incidents and notifications; both are visible in the dashboard and
pushed over SignalR.

## 9. Feature delivery observability

Every installation job is observable end to end, live:

```
Customer:  Cafe 1
Feature:   Advanced Analytics        Version: 1.4.0
Status:    Installing                Step:    Migration 2/4
Started:   12:41                     Elapsed: 00:01:12
```

On completion:

```
Status: Installed     Health: healthy      Duration: 00:02:40
```

On failure:

```
Status:   Failed          Step:     migrate (5/9)
Reason:   ProgrammingError: relation "analytics_report" already exists
Rollback: Succeeded  (or: ManualInterventionRequired — migration 0003 is irreversible)
Incident: INC-142
```

Requirements:

- Per-step progress is pushed to KNIGHT and relayed over SignalR (`jobProgress`).
- Step output is captured, truncated, and **scrubbed** before storage; secrets
  from the job payload never appear in it.
- Every job carries a correlation id shared by the dashboard action, the
  KNIGHT job record, the agent's log lines, and the store's own logs.
- A failed installation raises an alert; repeated or production failures open an
  incident. A "paid but not installed" state is a monitored condition, not a
  silent inconsistency.
- Drift detection: what a store reports as installed is periodically compared
  with what KNIGHT believes; mismatches raise `feature.drift`.

## 10. Dashboard-facing guarantee

Any error shown in the dashboard must let an operator answer: which customer,
which store, which environment, which version, which endpoint, when it started,
how often, and whether an incident exists — without leaving the page.
