# Phase 9 — verification

Status: **done**, 2026-08-20. Everything below was carried out against a running
stack, not a test host.

## 1. What was built

| TODO item | Where it lives |
|---|---|
| `ProvisioningJob` and the provisioning flow | `backend/modules/Provisioning`, [`adr/0025`](adr/0025-provisioning-is-a-job-with-manual-steps.md) |
| Versioned, signed base store image | `FeatureRegistry.Domain.StoreImage`, `/api/v1/store-images` |
| Automated base-Feature installation at provisioning | `BaseFeatureInstaller`, the `base-features` step |
| Dedicated-server metadata; optional mTLS | `Server.DedicatedCustomerId`, `Store.MutualTlsThumbprint`, `MutualTlsGate` |
| Backup status reporting and `backup.failed` alerting | `StoreBackup`, `POST /api/v1/ingest/backups`, [`adr/0026`](adr/0026-knight-records-backups-it-does-not-take-them.md) |
| Deprovisioning: disable → revoke → retain → export → purge | the deprovisioning pipeline |
| Per-customer retention overrides by plan | `Customer.DataRetentionOverrideDays`, `Plan.DataRetentionDays` |
| Publish a Feature version from the dashboard | `POST /api/v1/artifacts` upload + the store-images screen |
| Outbound email and activation links | `IEmailSender`, `AccountInvitationSender`, `POST /api/v1/auth/activate` |

## 2. How to run it

```bash
# Infrastructure (or any PostgreSQL — see development.md §2).
docker compose -f infrastructure/docker/docker-compose.yml up -d

# Schema.
cd backend
CONTROL_PLANE_DB_CONNECTION_STRING="Host=127.0.0.1;Port=5433;Database=knight;Username=knight;Password=knight" \
  dotnet ef database update --project src/Knight.Infrastructure --startup-project src/Knight.Api --context ControlPlaneDbContext

# First administrator (password typed in, never an argument).
dotnet run --project tools/Knight.Bootstrap -- --email you@example.com

# API.
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Knight.Api --urls http://localhost:5008

# Dashboard, against the real API.
cd ../frontend/knight-dashboard
cp .env.example .env.local     # set VITE_USE_MOCKS=false
npm run dev
```

Sign in at `http://localhost:5173`. A `SuperAdmin` must enrol a second factor on
its first sign-in before it can reach anything else.

## 3. What to click, and what should happen

### 3.1 A provisioning run stops where it honestly cannot continue

1. Customers → **New customer** → activate it.
2. Stores → **New store** for that customer, environment *Production*, hosting
   *Dedicated (managed)*.
3. Open the store → **Provisioning** tab → **Start provisioning**.

   Expected: the run is *Waiting for a person* on **Machine**, with the text
   "No machine is recorded for this store yet. Register the server and assign it
   to the store."

4. Click **Record as done** on the Machine step without assigning a server.

   Expected: refused (409) — "Assign the store to a registered server before
   recording this step."

5. Infrastructure → register a server (*Dedicated (managed)*, *Production*),
   dedicate it to the customer, then edit the store and set that server.
6. Back on **Provisioning**, record **Machine** as done.

   Expected: the run moves to **Store instance**, still manual.

7. Record **Store instance** as done, naming base image `2.3.0` (§3.2).

   Expected: the run walks through *Store registered* automatically and stops on
   **Credentials** — "No usable credential. Issue one from the store's
   credentials page — the secret is shown once and is never stored."

8. Credentials tab → **Issue credentials** → back to Provisioning →
   **Check again**.

   Expected: *Credentials* is Done, and the run now waits on **Agent enrolled**.
   It will stay there until an agent on that server enrols — which is correct:
   the store is not Active and must not be.

An operator can never tick off an automatic step. Try it on **Health check**:
the API refuses with "Step 'healthcheck' is carried out by KNIGHT and cannot be
ticked off by hand."

### 3.2 Publishing a base store image

Signing is offline; the dashboard never holds a key.

```bash
# Package and sign (features/tools/knight_package.py does this for Features;
# the same detached signature over the digest applies to an image).
KNIGHT_SIGNING_KEY=<base64 PKCS#8 private key> python features/tools/knight_package.py sign <digest>
```

Store images → fill *Image version* and *Store version it pins*, **Choose the
package**, paste the detached signature, **Register an image**, then **Publish**
in the row.

Expected: the row shows `Draft` then `Published`, with the digest KNIGHT
computed and the signing key id. A signature that does not verify is refused
with "The signature over the digest is not valid for signing key '…'".

### 3.3 Mutual TLS on a dedicated store

Provisioning tab → *Mutual TLS* → paste a sha-256 thumbprint → **Save**.

```bash
# Refused: the credential alone is no longer enough.
curl -i -X POST http://localhost:5008/api/v1/ingest/handshake \
  -H 'Content-Type: application/json' \
  -d '{"clientId":"…","clientSecret":"…","environment":"Production"}'          # 401

# Accepted: the proxy's header carries the bound certificate.
curl -i -X POST http://localhost:5008/api/v1/ingest/handshake \
  -H 'Content-Type: application/json' \
  -H 'X-Client-Certificate-Sha256: <thumbprint>' \
  -d '{"clientId":"…","clientSecret":"…","environment":"Production"}'          # 200
```

The same check runs on every authenticated ingest call, not only the handshake.
The field is not offered for a store on shared hosting, and the API refuses it
there.

### 3.4 Backups

```bash
curl -X POST http://localhost:5008/api/v1/ingest/backups \
  -H "Authorization: Bearer <store token>" \
  -H 'X-Client-Certificate-Sha256: <thumbprint, if bound>' \
  -H 'Content-Type: application/json' \
  -d '{"environment":"Production","status":"Succeeded","kind":"Scheduled",
       "startedAt":"2026-08-20T02:00:00Z","completedAt":"2026-08-20T02:04:00Z",
       "sizeBytes":1048576,"location":"s3://knight-backups/acme/2026-08-20.dump"}'
```

Expected: the report appears under *Backups the store reported* on the
Provisioning tab. Sending `"status":"Succeeded"` with `sizeBytes: 0` is refused
(400). Sending `"status":"Failed"` raises **backup.failed** immediately — visible
on Alerts with the store's name and the store's own failure text. A store nobody
has reported a successful backup for raises **backup.overdue** from the
observability sweep within a minute.

### 3.5 Deprovisioning and retention

1. Customers → the customer → *Data retention* → set **14** → **Save**.
   Expected: the row reads "14 days".
2. The store's Provisioning tab → **Cancel run** (a store may only have one
   unfinished run) → **Deprovision**.

   Expected: *Disable Features*, *Revoke access* and *Stop ingestion* complete
   at once, and the run waits on **Retention window** — "The store's data is
   retained until 2026-09-03…", fourteen days out, from the override rather than
   the plan.
3. Check the store: `Archived`, integration `Disconnected`, every credential
   `Revoked`.

The remaining steps are *Export* (manual — produce the customer's backup and
hand it over) and *Purge*, which the coordinator runs once the window closes.

### 3.6 An invited administrator

With `Email:Host` and `Email:DashboardBaseUrl` configured, creating an account
sends an activation link and returns no password (`invitationSent: true`).
Following the link opens `/activate`, which asks for a password twice and issues
no session — the account then signs in normally.

With no mail transport configured, account creation falls back to the one-time
password it always had and says so (`invitationSent: false`). Nothing pretends a
message was sent.

## 4. What this run found and fixed

- The backup and store-image list endpoints answered a bare JSON array, which
  the dashboard's collection hook cannot consume. Both now answer an `items`
  envelope like every other collection. **Only a browser finds this**; the
  integration test that covered it was reading the array directly, and now reads
  the envelope.
- A manual step completed with no note kept the "waiting for…" text beside a
  *Done* status. A step recorded with no note now says "Recorded by an operator."
- The Machine step could be ticked off while no server was recorded against the
  store, leaving the run to stall two steps later at the agent step for a reason
  nobody could act on. It is now refused, with the fix in the message.
- A run had no way to be closed, so a store whose provisioning stalled could
  never be deprovisioned — the second run is refused while the first is
  unfinished. The panel now offers **Cancel run**.
- The mutual-TLS card was offered on an archived store, where the aggregate can
  only refuse. It is now hidden there.

## 5. Test results

```
REQUIRE_POSTGRES_TESTS=1 KNIGHT_TEST_POSTGRES=… dotnet test Knight.slnx

Knight.UnitTests          551 passed, 0 failed
Knight.ArchitectureTests   13 passed, 0 failed
Knight.IntegrationTests   133 passed, 0 failed
```

Dashboard: `npx tsc --noEmit` clean, `npm run build` clean, `npm run test` 9
passed.

## 6. Known limits

- Creating the machine, building the store instance, and wiring DNS and TLS are
  manual steps by design in this release
  ([`adr/0025`](adr/0025-provisioning-is-a-job-with-manual-steps.md)). They are
  represented on the run rather than pretended away.
- The `export` step of a deprovisioning run is manual: KNIGHT holds no store
  data and cannot produce the export itself.
- Mutual TLS trusts the terminating proxy's header. A deployment where the proxy
  does not strip that header from unverified requests must leave it off.
- SMTP is the only mail transport. There is no queue in front of it: a
  notification that cannot be delivered is recorded as failed, and an invitation
  that cannot be sent falls back to a one-time password.
