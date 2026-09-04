# Phase 30 F — the customer portal, verified

The self-service **customer portal** (docs/self-service-saas-plan.md §12, F): a
separate, role-gated route tree from the operations dashboard. A customer
principal lands in the portal; platform staff land in the operations dashboard;
each is redirected off the other's tree.

## What it adds

**Backend** — the customer `/me` self-service API (`ControlPlaneMeEndpoints`):

- `GET /api/v1/catalog/plans` — the public price list (moved off `/api/v1/plans`,
  which stays the operator plan list).
- `POST /api/v1/billing/checkout` — server-priced checkout (phase C).
- `GET /api/v1/me/subscription`, `POST /api/v1/me/subscription/cancel`
  (cancel-at-period-end — a new soft cancel on the subscription service).
- `GET /api/v1/me/stores`, `GET /api/v1/me/stores/{id}`,
  `GET /api/v1/me/stores/{id}/provisioning` (a friendly, per-step projection of
  the provisioning run).

**Frontend** — `frontend/knight-dashboard/src/features/portal/`:

- public sign-up and email-verification pages (no account-existence oracle);
- a plan catalogue with a CUSTOM optional-feature selector and a checkout;
- a portal home showing the subscription and the store, with live provisioning
  progress that polls until the store is ready;
- a store page with the friendly provisioning timeline;
- `PortalLayout`, and `RoleLayout` in `app/App.tsx` that routes by principal.

## Found by driving it in a browser, and fixed

The browser walk found three defects the type-checker and the backend acceptance
test could not:

1. **`/me/subscription` returns 204 for a customer who has not bought yet**, which
   the API client reads as `undefined` — and TanStack Query throws on a query
   function returning `undefined`. The portal home errored on load. Normalised to
   `null`.
2. **The simulated infrastructure worker only swept `Running` provisioning jobs.**
   A run waiting on a manual step sits in `AwaitingOperator`, not `Running`, so
   every self-service store stalled at "needs a manual step" and never came up.
   The backend acceptance test had masked this by listing jobs in *all* states and
   driving them by hand; the worker now sweeps `Running` **and**
   `AwaitingOperator`.
3. **The provisioning `Meter` showed 1% instead of 100%** — the component takes a
   0–100 value and the pages divided by 100.

## How to verify it again

```bash
# 1. Infrastructure (Postgres 5433, Redis 6379).
docker compose -f infrastructure/docker/docker-compose.yml up -d

# 2. Control-plane schema.
CONTROL_PLANE_DB_CONNECTION_STRING="Host=localhost;Port=5433;Database=knight;Username=knight;Password=knight" \
  dotnet ef database update --project backend/src/Knight.Infrastructure \
  --startup-project backend/src/Knight.Api --context ControlPlaneDbContext

# 3. A publicly purchasable plan (no dashboard screen edits the public flag yet):
docker exec <postgres> psql -U knight -d knight -c \
  "INSERT INTO control.plans (\"Id\",\"Key\",\"Name\",\"Description\",\"BasePriceAmount\",\"Currency\",\"IsActive\",\"SortOrder\",\"CreatedAt\",\"IsPubliclyPurchasable\") \
   VALUES (gen_random_uuid(),'starter','Starter','A simple store to get you selling.',29.00,'EUR',true,1,now(),true);"

# 4. The API, with infrastructure simulated.
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5008 \
  CONTROL_PLANE_DB_CONNECTION_STRING="Host=localhost;Port=5433;Database=knight;Username=knight;Password=knight" \
  Provisioning__SimulateInfrastructure=true \
  dotnet run --project backend/src/Knight.Api

# 5. The dashboard (frontend/knight-dashboard/.env already sets VITE_USE_MOCKS=false).
npm --prefix frontend/knight-dashboard run dev
```

Then, at `http://localhost:5173`:

1. **Sign up** at `/signup` (name, email, a 12+ character password). Expect the
   "check your email" screen and a `202` from `POST /auth/register`.
2. **Verify.** With no mail transport configured in development, mark the account
   verified out of band — the verification flow itself is covered by
   `SelfServiceRegistrationTests`:
   `UPDATE control.users SET "EmailVerified"=true, "Status"='Active' WHERE "NormalizedEmail"='<EMAIL IN CAPS>';`
3. **Sign in** at `/` with the same credentials. A customer principal lands in the
   portal (not the operations dashboard).
4. **Choose a plan** → `/portal/plans`, select Starter, **Continue to checkout**.
   Expect a `200` and the "Almost there" screen with the server-computed amount.
5. **Simulate the payment** the provider would confirm:
   `curl -X POST http://localhost:5008/api/v1/billing/webhooks/simulated -H "Content-Type: application/json" -d '{"type":"payment_succeeded","providerSessionId":"<from control.checkout_sessions>","providerTransactionId":"t1"}'`
   Expect `{"status":"processed"}`.
6. **Watch it come up.** The portal home shows the subscription **Active** and the
   store provisioning; within a few seconds the simulated worker drives it to
   **Ready** at **100%**, and `/portal/stores/{id}` shows the friendly step
   timeline.
7. **Cancel** from the subscription card: `POST /me/subscription/cancel` returns
   `204`, the subscription stays **Active**, and the chip changes to "Ends at
   period end".

**Verified on 2026-09-04** by driving all seven steps in a browser against the
live API.

## Still ahead

- **G — operations**: the operator-side provisioning retry/resume, entitlement
  grant/revoke, suspend/restore and audit screens.
- A dashboard screen (or seed) to mark a plan publicly purchasable, so step 3
  above is not a manual SQL insert.
- Wiring a development mail transport (or a captured-token dev endpoint) so the
  verification step needs no SQL.
