# API Contracts

Status: **authoritative proposal**. Endpoint shapes may be refined during
implementation, but the conventions below are binding.

There are four distinct contracts:

```
1. Dashboard  ->  KNIGHT      /api/v1/...
2. KNIGHT     <-> Store       /api/knight/...  (on the store) and /api/v1/ingest/... (on KNIGHT)
3. Agent      <-> KNIGHT      /api/v1/agent/...   (metrics push + lifecycle job polling)
4. Agent      ->  Registry    signed artifact download (digest + signature verified)
```

## 1. Conventions

- Base path is versioned: `/api/v1`. A breaking change means `/api/v2`, never a
  silent change.
- JSON only, `camelCase` properties, ISO-8601 UTC timestamps with `Z`.
- Every response carries `X-Correlation-Id`; clients may supply one.
- Collections are paged: `?page=1&pageSize=25&sort=-createdAt&q=...`, returning

```json
{ "items": [], "page": 1, "pageSize": 25, "totalCount": 0, "totalPages": 0 }
```

- Mutating store/agent ingestion endpoints accept `Idempotency-Key`.
- No database entity is ever serialised directly; all payloads are DTOs from
  `Knight.Contracts`.

### Error contract (RFC 7807 + extensions)

```json
{
  "type": "https://knight.dev/errors/validation-failed",
  "title": "Validation failed",
  "status": 400,
  "code": "validation_failed",
  "detail": "One or more fields are invalid.",
  "requestId": "0HMV9...",
  "validationErrors": { "domain": ["Domain is already registered."] }
}
```

Stack traces are never returned. Internal exception details are logged with the
same `requestId` and nothing else leaks.

Standard codes: `unauthorized`, `forbidden`, `not_found`, `conflict`,
`validation_failed`, `rate_limited`, `feature_not_entitled`,
`store_unreachable`, `internal_error`.

Feature-delivery codes: `feature_incompatible`,
`feature_dependency_unsatisfied`, `feature_version_yanked`,
`feature_already_installed`, `feature_install_in_progress`,
`feature_has_dependents`, `manifest_invalid`, `artifact_verification_failed`,
`job_conflict`, `rollback_manual_intervention_required`.

## 2. Dashboard → KNIGHT

### Auth
```
POST   /api/v1/auth/login              { email, password }        -> { accessToken, refreshToken, expiresIn, user }
POST   /api/v1/auth/refresh            { refreshToken }
POST   /api/v1/auth/logout
GET    /api/v1/auth/me                                            -> user + roles + permissions + customerId?
```

### Customers
```
GET    /api/v1/customers
POST   /api/v1/customers
GET    /api/v1/customers/{id}
PATCH  /api/v1/customers/{id}
POST   /api/v1/customers/{id}/suspend
POST   /api/v1/customers/{id}/activate
POST   /api/v1/customers/{id}/archive
```

### Stores
```
GET    /api/v1/stores?customerId=&environment=&status=
POST   /api/v1/stores
GET    /api/v1/stores/{id}
PATCH  /api/v1/stores/{id}
POST   /api/v1/stores/{id}/activate | /suspend | /archive
GET    /api/v1/stores/{id}/credentials          -> by state; never a secret or a hash
POST   /api/v1/stores/{id}/credentials          -> issues clientId + secret (secret shown once)
POST   /api/v1/stores/{id}/credentials/{cid}/rotate
DELETE /api/v1/stores/{id}/credentials/{cid}    -> revoke
GET    /api/v1/stores/{id}/health?limit=        current link state + the observations behind it
GET    /api/v1/stores/{id}/deployments
GET    /api/v1/stores/{id}/events               lifecycle events the store reported
GET    /api/v1/stores/{id}/errors               raw error stream (grouping is phase 5)
GET    /api/v1/stores/{id}/activity
GET    /api/v1/stores/{id}/domains
GET    /api/v1/stores/{id}/domain-verification  the outstanding challenge, if any
POST   /api/v1/stores/{id}/domain-verification         issues a token
POST   /api/v1/stores/{id}/domain-verification/verify  fetches and checks it
GET    /api/v1/stores/{id}/entitlements
```

A store reaches `integrationStatus: Connected` only once its primary domain has
been verified ([`adr/0021`](adr/0021-domain-verification-before-connected.md));
until then a successful handshake leaves it `Pending`.

### Feature registry
```
GET    /api/v1/features                          ?status=&category=&search=
POST   /api/v1/features                          create a feature identity
PATCH  /api/v1/features/{id}                     metadata, deprecate, withdraw
GET    /api/v1/features/{id}/versions
POST   /api/v1/features/{id}/versions            register a built artifact
                                                 { version, packageReference,
                                                   artifactDigest, signature, manifest }
GET    /api/v1/features/{id}/versions/{v}
POST   /api/v1/features/{id}/versions/{v}/publish
POST   /api/v1/features/{id}/versions/{v}/yank   { reason }
POST   /api/v1/features/manifest/validate        dry-run manifest validation
GET    /api/v1/features/{id}/versions/{v}/dependents   which features/stores are affected
```

Versions are immutable once published; `PATCH` on a published version is
rejected with `409 conflict`.

### Feature installation and jobs
```
GET    /api/v1/stores/{id}/features                     installed + entitled + state
GET    /api/v1/stores/{id}/features/{featureId}
POST   /api/v1/stores/{id}/features/{featureId}/install   { version? } -> job
POST   /api/v1/stores/{id}/features/{featureId}/upgrade   { version }  -> job
POST   /api/v1/stores/{id}/features/{featureId}/enable    -> job
POST   /api/v1/stores/{id}/features/{featureId}/disable   -> job
POST   /api/v1/stores/{id}/features/{featureId}/uninstall { purgeData: false } -> job
POST   /api/v1/stores/{id}/features/{featureId}/rollback  -> job
PUT    /api/v1/stores/{id}/features/{featureId}/configuration  { values }
POST   /api/v1/stores/{id}/features/{featureId}/plan      dry-run: resolved install plan,
                                                          dependencies, compatibility verdict

GET    /api/v1/jobs?storeId=&status=&type=
GET    /api/v1/jobs/{id}                 state, plan, currentStep/totalSteps, step results
POST   /api/v1/jobs/{id}/retry
POST   /api/v1/jobs/{id}/cancel          only while Queued or Claimed
GET    /api/v1/stores/{id}/provisioning  provisioning job for this store
```

A mutating call returns `202 Accepted` with the created job:

```json
{ "jobId": "...", "type": "Install", "status": "Queued",
  "plan": [{ "slug": "knight-feature-analytics-core", "version": "1.2.3" },
           { "slug": "knight-feature-ai-reports",     "version": "2.0.1" }],
  "totalSteps": 9 }
```

If dependency resolution or compatibility fails, **no job is created** and the
response is `409` with `code: "feature_incompatible"` or
`"feature_dependency_unsatisfied"` plus the failing constraint in `details`.

### Plans, subscriptions, billing
```
GET    /api/v1/plans
POST   /api/v1/plans
PATCH  /api/v1/plans/{id}
PUT    /api/v1/plans/{id}/features           { featureId, isIncluded, isCustomerToggleable, pinnedVersionRange? }

GET    /api/v1/subscriptions?customerId=
POST   /api/v1/subscriptions                       { customerId, planId, featureKeys[] }
PATCH  /api/v1/subscriptions/{id}                  change plan
PUT    /api/v1/subscriptions/{id}/features         { featureKeys[] }
POST   /api/v1/subscriptions/{id}/cancel
POST   /api/v1/subscriptions/quote                 price preview, no side effects

GET    /api/v1/invoices?customerId=&status=
GET    /api/v1/invoices/{id}
POST   /api/v1/invoices/{id}/issue
POST   /api/v1/invoices/{id}/payments
```

### Infrastructure, monitoring, errors, incidents, logs
```
GET    /api/v1/servers                 GET /api/v1/servers/{id}/metrics?from=&to=&resolution=
POST   /api/v1/servers                 GET /api/v1/servers/{id}/agents
GET    /api/v1/monitoring/overview     aggregate status tiles for the dashboard

GET    /api/v1/errors/groups?storeId=&status=&search=
GET    /api/v1/errors/groups/{id}      GET /api/v1/errors/groups/{id}/events
POST   /api/v1/errors/groups/{id}/acknowledge | /resolve | /ignore

GET    /api/v1/incidents               POST /api/v1/incidents
PATCH  /api/v1/incidents/{id}          POST /api/v1/incidents/{id}/events
GET    /api/v1/logs?storeId=&level=            requires logs.view; shipped by stores

GET    /api/v1/audit-logs?actorId=&targetType=&from=&to=
GET    /api/v1/reports/{key}
```

### Real-time (SignalR hub `/hubs/dashboard`)
```
server -> client:  storeStatusChanged, serverStatusChanged, incidentOpened,
                   incidentResolved, errorGroupSpiked, deploymentCompleted,
                   jobProgress, jobCompleted, featureInstallationStateChanged
client -> server:  subscribeToCustomer(customerId)   (authorised server-side)
```

## 3. KNIGHT ↔ Store

### Implemented by the store (Django), called by KNIGHT
```
GET  /api/knight/health       -> { status, checkedAt, version, environment,
                                   dependencies: { database, redis, worker } }
GET  /api/knight/version      -> { version, commit?, deployedAt, environment }
GET  /api/knight/status       -> lightweight summary for dashboards
POST /api/knight/features     -> KNIGHT pushes the effective entitlement set
```

> The store-side surface is read/notify only. **Installation is never performed
> by an inbound HTTP call to the store** — it is executed by the agent from a
> polled job ([`adr/0015`](adr/0015-feature-delivery-mechanism.md)).

### Implemented by KNIGHT, called by the store
```
POST /api/v1/ingest/handshake   store proves credentials, receives a short-lived token
POST /api/v1/ingest/errors      batch of error events
POST /api/v1/ingest/events      deployment/backup/business-neutral lifecycle events
POST /api/v1/ingest/logs        batch of structured log entries (entitlement-gated)
GET  /api/v1/ingest/features    store pulls its effective entitlements, signed
POST /api/v1/ingest/heartbeat   store liveness when outbound-only networking is required
```

Everything but the handshake carries the store token
([`adr/0020`](adr/0020-store-ingestion-authentication.md)). Three rules apply to
every call and are enforced in one place rather than per endpoint:

- **The store's identity comes from the token, never from the payload.** A body
  naming a different `storeId` is not an error to report; it is a field that is
  never read.
- **The payload's `environment` must match the token's.** They can only diverge
  if a store is misconfigured or a token is being used by something else.
- **Rate limits are per store, not per address.** Stores share egress addresses,
  and one store looping must not silence its neighbours.

A refused handshake answers `401` with one body whatever was wrong with it —
unknown client id, wrong secret, revoked credential, suspended store, suspended
customer, mismatched environment. Which check failed is in the audit log, where
only an operator can read it.

The entitlement set is signed so a store can keep enforcing it while KNIGHT is
unreachable. The canonical form is
`knight-entitlements|1|{storeId}|{customerId}|{environment}|{issuedAt}|{staleAfter}|{slug}:{expiresAt or -},...`,
features sorted by slug with an ordinal comparison, timestamps as Unix seconds.
Both sides test it against
[`contracts/store-integration.samples.json`](contracts/store-integration.samples.json).

### Error ingestion payload
```json
{
  "environment": "Production",
  "version": "2.4.1",
  "events": [{
    "occurredAt": "2026-08-18T10:20:31Z",
    "exceptionType": "IntegrityError",
    "message": "duplicate key value violates unique constraint",
    "endpoint": "/api/orders/",
    "httpMethod": "POST",
    "statusCode": 500,
    "stackTrace": "...",
    "requestId": "abc123",
    "traceId": "4bf92f...",
    "context": { "userAgent": "...", "release": "2.4.1" }
  }]
}
```

Response: `202 Accepted` with
`{ "accepted": 1, "rejected": 0, "duplicate": false, "errors": [] }`.

A batch may carry an `Idempotency-Key`; one replayed
under a key already seen is acknowledged as `duplicate` without being written
twice, because from the store's point of view it did arrive. A malformed event
inside an otherwise valid batch is counted and described in the receipt rather
than costing the store the whole batch.

## 4. Agent ↔ KNIGHT
```
POST /api/v1/agent/handshake     agent registers with a one-time provisioning token
POST /api/v1/agent/heartbeat     { agentVersion, uptimeSeconds, storeVersion, runtime }
POST /api/v1/agent/metrics       batched ServerMetric samples
POST /api/v1/agent/events        service/container state transitions

GET  /api/v1/agent/jobs/next     long-poll for the next job for this agent's store
POST /api/v1/agent/jobs/{id}/claim
POST /api/v1/agent/jobs/{id}/progress   { stepIndex, name, status, output }
POST /api/v1/agent/jobs/{id}/result     { status, rollbackOutcome?, errorCode?, health }
```

A job payload is **typed data, never a command string**:

```json
{
  "jobId": "...", "type": "Install", "storeId": "...",
  "steps": ["preflight","fetch","backup","install","migrate",
            "configure","enable","reload","healthcheck"],
  "target": {
    "slug": "knight-feature-analytics", "version": "1.4.0",
    "packageReference": "knight-feature-analytics==1.4.0",
    "artifactDigest": "sha256:…", "signature": "…"
  },
  "dependencies": [{ "slug": "knight-feature-analytics-core", "version": "1.2.3" }],
  "configuration": { "language": "fa", "schedule": "daily" },
  "constraints": { "requiresRestart": true, "timeoutSeconds": 900 }
}
```

The agent rejects any job type it does not implement. KNIGHT never sends shell
commands, scripts, or arbitrary URLs; artifacts come only from the registered
package registry and are verified against `artifactDigest` and `signature`
before use. Configuration secrets are delivered inside the job payload over TLS
and are never echoed back in progress or result reports.

## 5. Documentation

OpenAPI is generated for `/api/v1` and served with Scalar in Development. The
store-side `/api/knight/*` contract is documented in `store-integration.md` and
mirrored by a schema file so both sides can be tested against it.
