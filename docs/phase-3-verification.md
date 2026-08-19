# Phase 3 — how to verify it

Status: **authoritative**. Follow it top to bottom on a clean machine; every
command is one somebody actually ran.

Paths use forward slashes and `.venv/Scripts/python` (Windows). On POSIX use
`.venv/bin/python`.

## 0. What you need

.NET 10 SDK, Python 3.12+, Node 20+, and a PostgreSQL. No Docker, no Redis.
If you have no PostgreSQL, [`development.md`](development.md) §2 starts one from
the binaries-only distribution in a directory.

Two databases, once:

```bash
createdb -h 127.0.0.1 -p 5433 -U knight knight
createdb -h 127.0.0.1 -p 5433 -U knight refstore
```

## 1. Bring KNIGHT up

```bash
cd backend
export CONTROL_PLANE_DB_CONNECTION_STRING="Host=127.0.0.1;Port=5433;Database=knight;Username=knight;Password=knight"
export ConnectionStrings__Platform="$CONTROL_PLANE_DB_CONNECTION_STRING"
export ConnectionStrings__ControlPlane="$CONTROL_PLANE_DB_CONNECTION_STRING"

dotnet ef database update --project src/Knight.Infrastructure --startup-project src/Knight.Api --context PlatformDbContext
dotnet ef database update --project src/Knight.Infrastructure --startup-project src/Knight.Api --context ControlPlaneDbContext

dotnet run --project tools/Knight.Bootstrap -- --control-plane --email admin@knight.test
#   password: anything 10–128 characters, typed twice

dotnet run --project src/Knight.Api --urls http://localhost:5008
```

**Expected:** on startup, `Replay protection and caching are running in-process
because no Redis connection string is configured` and `Store health polling every
30s`. The first is development-only and refused in any other environment; the
second is the poller arming.

## 2. Bring the dashboard up

```bash
cd frontend/knight-dashboard
npm install
cp .env.example .env.local     # set VITE_USE_MOCKS=false
npm run dev
```

Open <http://127.0.0.1:5173>, sign in as `admin@knight.test`.

**Expected:** the account holds `SuperAdmin`, so the first sign-in demands MFA
enrolment. Scan the QR code (or the secret) into an authenticator and confirm.
Subsequent sign-ins ask for the six-digit code.

## 3. Register a store

In the dashboard:

1. **Customers → New**: name `Cafe Parthia`, contact `owner@cafeparthia.test`.
   Activate it.
2. **Stores → New**: customer `Cafe Parthia`, name `Cafe Parthia storefront`,
   slug `cafe-parthia`, domain `reference-store.knight.test`, environment
   `Development`, hosting `SharedManaged`. Activate it.
3. On the store, **issue a credential**. Copy the client id and the secret.
   **The secret is shown once.**
4. Give the customer a subscription on the **Professional** plan.

**Expected:** the store reads `Provisioning → Active`, integration
`NotRegistered`, and the credential appears under Credentials as `Active` with
no secret anywhere in the response.

## 4. Bring the store up and register it

`stores/reference-store/.env`:

```
KNIGHT_BASE_URL=http://localhost:5008
KNIGHT_CLIENT_ID=<from step 3>
KNIGHT_CLIENT_SECRET=<from step 3>
KNIGHT_ENVIRONMENT=Development
STORE_VERSION=1.0.0
STORE_DB_NAME=refstore
STORE_DB_HOST=127.0.0.1
STORE_DB_PORT=5433
STORE_DB_USER=knight
STORE_DB_PASSWORD=knight
```

```bash
cd stores/reference-store
python -m venv .venv
.venv/Scripts/python -m pip install -r requirements.txt
.venv/Scripts/python manage.py migrate
.venv/Scripts/python manage.py knight_register
```

**Expected:**

```
Registered. Store <id>, environment Development.
Integration status: Pending
This store is Pending, not Connected: its primary domain has not been proven yet.
```

That is the point of [`adr/0021`](adr/0021-domain-verification-before-connected.md),
not a failure.

## 5. Prove the domain

In the dashboard, on the store, start domain verification. Put the token it
issues into the store's environment as `KNIGHT_DOMAIN_VERIFICATION_TOKEN`, then:

```bash
.venv/Scripts/python manage.py runserver 127.0.0.1:8000
```

Back in the dashboard, **Verify**.

**Expected:** `verified: true`, method `HttpToken`. The store's integration
status becomes `Connected` on its next contact. `curl
http://127.0.0.1:8000/.well-known/knight-domain-verification` returns the token;
`curl http://127.0.0.1:8000/api/knight/health` returns **401**, because the
health endpoint is signature-authenticated.

Local development reaches the store because
`appsettings.Development.json` maps `reference-store.knight.test` to
`http://127.0.0.1:8000` and permits private addresses. Both default to refusing
everywhere else.

## 6. Drive it

```bash
.venv/Scripts/python manage.py knight_selftest
```

**Expected:** six `ok` lines — configuration, dependencies, handshake, heartbeat,
entitlements, error reporting — and `Every step passed.` The entitlements line
lists what the Professional plan includes: `analytics, log-shipping,
order-management, storefront`.

Then, in a browser:

| URL | Expected |
|---|---|
| <http://127.0.0.1:8000/> | The catalogue, with `loyalty: not entitled` and `analytics: entitled, not installed` |
| <http://127.0.0.1:8000/loyalty/> | **402** `feature_not_entitled` — enforced server-side, from a signed payload |
| <http://127.0.0.1:8000/boom/> | **500**, and the exception reaches KNIGHT within ~10s |

Within 30 seconds the poller reaches the store on its own.

**Expected in the dashboard**, on the store:

- **Overview**: integration `Connected`, version `1.0.0`, last seen seconds ago.
- **Health**: observations from `Handshake`, `Heartbeat` and `Poll`, with the
  store's dependency block and reported features.
- **Errors**: `RuntimeError — The reference store raised this deliberately`,
  endpoint `/boom/`, method `GET`, status 500, with a stack trace and **no**
  request body, cookies or authorization header.
- **Deployments**: `1.0.0`, source `VersionChange`, status `Detected`.

## 7. Deployments

Stop the store, set `STORE_VERSION=1.1.0`, and:

```bash
.venv/Scripts/python manage.py shell -c "from knight_integration.events import report_deployment; print(report_deployment(version='1.1.0', previous_version='1.0.0'))"
```

**Expected:** `{'accepted': 1, ...}`, and **one** deployment row for `1.1.0` in
the dashboard — source `StoreReported`, status `Succeeded`. Not two: KNIGHT
detects the version change in the same handshake that carried the report, and
one deployment is one row.

## 8. The suites

```bash
cd backend
KNIGHT_TEST_POSTGRES="Host=127.0.0.1;Port=5433;Database=postgres;Username=knight;Password=knight" \
REQUIRE_POSTGRES_TESTS=1 dotnet test

cd ../stores/reference-store
.venv/Scripts/python manage.py test knight_integration
```

Both sides validate against `docs/contracts/store-integration.schema.json` and
the worked signature examples beside it, so a contract change that breaks either
side fails on both.

## 9. What is deliberately not there yet

A screen that cannot work should say so rather than look broken, so:

- **Usage charts on the store overview** say usage metrics arrive with server
  monitoring. There is no metric source until phase 4.
- **The logs screen** is empty unless a store is entitled to log shipping and
  ships some; the empty state says exactly that.
- **Errors are ungrouped.** The raw stream is stored and shown. Fingerprinting,
  counts and a group lifecycle are phase 5
  ([`adr/0013`](adr/0013-error-grouping-strategy.md)).
- **Installations and jobs** are still fixtures. Feature delivery is phase 3.5.
