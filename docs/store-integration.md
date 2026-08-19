# Store Integration

Status: **authoritative**, and implemented — see
[`stores/reference-store/`](../stores/reference-store/README.md). The wire
contract both sides test against is
[`contracts/store-integration.schema.json`](contracts/store-integration.schema.json).

A customer store is an independent Django application. It talks to KNIGHT only
through the contract in `api-contracts.md`, from a dedicated integration layer
that is kept strictly separate from the store's business domain.

## 1. Layout inside a store

```
store/
├── apps/                       business domain — never imports knight_integration
│   ├── products/
│   ├── orders/
│   └── customers/
└── knight_integration/
    ├── __init__.py
    ├── apps.py
    ├── conf.py                 settings + environment validation
    ├── client.py               HTTP client to KNIGHT (retry, timeout, backoff)
    ├── auth.py                 credential handling, token cache, rotation
    ├── health/                 /api/knight/health, /version, /status views
    ├── features/
    │   ├── entitlements.py     entitlement cache + enforcement helpers
    │   ├── registry.py         which feature packages are installed and enabled
    │   ├── loader.py           dynamic INSTALLED_APPS / URL / settings composition
    │   └── config.py           per-feature configuration delivered by KNIGHT
    ├── installer/              executes lifecycle steps for a job (see §10)
    │   ├── steps.py            preflight, fetch, verify, install, migrate,
    │   │                       configure, enable, reload, healthcheck
    │   └── rollback.py
    ├── errors/                 exception middleware -> batched error reporting
    ├── events/                 deployment/backup lifecycle events
    ├── logs/                   optional structured log shipping
    ├── management/commands/    knight_register, knight_sync_features,
    │                           knight_apply_job, knight_selftest
    └── tests/
```

Rule: **business logic never imports the integration layer, and the integration
layer never imports business models.** Business code asks about a feature
through a thin façade (`knight_integration.features.is_enabled("x")`).

Installed **Feature packages** are a third category, separate from both: they
are independent Django apps (`knight_feature_*`) delivered by KNIGHT
([`feature-delivery.md`](feature-delivery.md)). They may use documented
extension points, but they never modify store business code, and store business
code never imports a feature package directly — it goes through the façade.

## 2. Lifecycle

```
0. Provision   Store instance created and base features installed
               (see store-provisioning.md).
1. Register    Platform admin creates the Store in KNIGHT and issues credentials
               (clientId + secret, secret displayed once).
2. Configure   Secret is placed in the store's environment (never in git).
3. Connect     `python manage.py knight_register` performs the handshake.
               The store reaches Pending. It reaches Connected only once its
               primary domain has been proven (section 12) — a credential says
               nothing about who answers on the domain KNIGHT polls
               (adr/0021).
4. Operate     - KNIGHT polls /api/knight/health on a schedule
               - the store pushes errors/events and pulls entitlements
               - the agent polls for lifecycle jobs and installs/upgrades
                 Feature packages
               - store version and installed feature versions are reported
5. Rotate      Credentials are rotated from the dashboard; the old secret stays
               valid for a grace period, then is revoked.
6. Retire      Store archived in KNIGHT; credentials revoked; ingestion refused.
```

## 3. Feature entitlements (the commercial half)

```
KNIGHT (source of truth)
   │  pull every N minutes  +  push on change
   ▼
knight_integration.features   cache (Redis or local, with TTL and a signed payload)
   ▼
Django business code          is_enabled("advanced_analytics")
```

Rules:

- The store enforces entitlements **server-side**. A frontend flag is never
  sufficient.
- The cached entitlement set has a TTL and a `staleAfter`. On prolonged failure
  to refresh, the store falls back to the **last known good** set for a bounded
  grace period and then to the plan's minimum safe set — it never fails open on
  paid features and never hard-crashes the storefront.
- Entitlement changes are logged locally and reported as an event.
- **Entitlement is not installation.** A feature package that is installed but
  not entitled must refuse to serve; an entitlement without an installation is
  reported to KNIGHT as a delivery gap, not silently ignored.

## 4. Error reporting

- A Django middleware plus a logging handler capture unhandled exceptions.
- Events are **batched** (size + time window) and sent asynchronously, so a
  slow or unavailable KNIGHT never adds latency to a customer request.
- A local bounded queue drops oldest events under sustained failure and records
  a drop counter — reporting must never exhaust store memory.
- Payloads are scrubbed: no passwords, tokens, card data, or full request
  bodies by default. A `SCRUB_KEYS` list is applied to context.

## 5. Health contract

`GET /api/knight/health` responds quickly and without touching business tables:

```json
{
  "status": "healthy",
  "checkedAt": "2026-08-18T12:41:00Z",
  "version": "2.4.1",
  "environment": "Production",
  "dependencies": {
    "database": { "status": "healthy", "latencyMs": 3 },
    "redis":    { "status": "healthy", "latencyMs": 1 },
    "worker":   { "status": "degraded", "detail": "queue backlog 1200" }
  }
}
```

The endpoint is authenticated and it must not touch business tables. KNIGHT
signs the request — `X-Knight-Signature`, an HMAC over method, path, timestamp
and nonce, under the key the store received in its handshake — and the store
refuses anything unsigned, stale beyond its skew window, or signed with another
key. The payload names versions, dependencies and installed features, which is
diagnostics for an operator and reconnaissance for everyone else.

The canonical string is
`knight-request|1|{METHOD}|{path}|{timestamp}|{nonce}`. The path only, never the
host: a proxy in front of the store may rewrite the host, and binding the
signature to it would break every store behind one.

## 6. Environment safety

Each store carries `KNIGHT_ENVIRONMENT`. The handshake fails if it does not
match the environment registered in KNIGHT. A production store can therefore
never report into a development control plane, even with valid credentials.

## 7. Configuration (environment variables)

```
KNIGHT_BASE_URL=https://knight.example.com
KNIGHT_CLIENT_ID=...
KNIGHT_CLIENT_SECRET=...        # secret store / env only, never committed
KNIGHT_ENVIRONMENT=Production
KNIGHT_STORE_ID=...
KNIGHT_ERROR_REPORTING=true
KNIGHT_LOG_SHIPPING=false
KNIGHT_FEATURE_REFRESH_SECONDS=300
KNIGHT_TIMEOUT_SECONDS=5
```

## 8. Reference implementation

[`stores/reference-store/`](../stores/reference-store/README.md) is a real
Django store: a minimal business app plus the full `knight_integration`
package. It is the integration-test target for the KNIGHT↔Store contract and the
worked example a customer store is built from.

The layering rule in section 1 is enforced there by a test rather than by
convention — `knight_integration/tests/test_boundaries.py` fails if a business
module reaches past the feature façade, or if the integration layer imports a
business model.

## 9. Feature loading

The store composes its runtime from installed feature packages instead of a
hand-edited settings file:

```
knight_integration.features.loader
  ├── reads the local installation registry (written only by the installer)
  ├── extends INSTALLED_APPS with enabled feature apps
  ├── includes each feature's URLs under its declared prefix
  ├── applies each feature's settings fragment and KNIGHT-delivered config
  └── exposes per-feature health checks to /api/knight/health
```

A feature that is installed but disabled is not loaded. A feature whose
entitlement is missing is loaded only far enough to report itself and refuse
service.

## 10. Installer responsibilities

Where an agent is present, the agent executes lifecycle jobs and calls the
store's `knight_apply_job` entry point for Django-specific steps (migrations,
configuration, enable/disable, health check). Where no agent exists (e.g.
customer-managed hosting without one), the same steps run from the management
command, driven by the integration layer polling for jobs.

Either way the step vocabulary is fixed and typed
([`adr/0015`](adr/0015-feature-delivery-mechanism.md)); the store never
executes a command string received from KNIGHT.

## 11. Domain ownership

KNIGHT calls the store, at an address an operator typed into a form and DNS
nobody in this system controls resolves. Before it will treat the link as
established, whoever controls that domain has to prove it holds a token only
KNIGHT issued ([`adr/0021`](adr/0021-domain-verification-before-connected.md)):

```
Operator starts verification in KNIGHT     -> token issued for this store
Store publishes it                          /.well-known/knight-domain-verification
                                            or TXT on _knight-verification.<domain>
KNIGHT fetches and compares it              -> integrationStatus Pending -> Connected
```

The token is not a credential — publishing it is the entire mechanism — and the
store serves it unauthenticated, because this runs before the store holds any key
to authenticate anything with. Changing a store's primary domain drops the proof.

Every outbound call KNIGHT makes to a store is checked at the socket: a resolved
address in a loopback, link-local, private or CGNAT range is refused before
connecting, and link-local is refused even where private ranges are explicitly
allowed.

## 12. Non-goals

- KNIGHT never queries a store database.
- KNIGHT never executes arbitrary code or shell commands on a store — only the
  fixed, typed lifecycle steps.
- The integration layer never becomes a place to put business rules.
- Feature source code is never copied into a store repository by hand.
