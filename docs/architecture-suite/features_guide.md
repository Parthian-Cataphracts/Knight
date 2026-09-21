# KNIGHT — Feature-by-feature guide (generated summary)

> Plain-language guides for the platform's core capabilities, mapped to the code at
> the end. For the merchant-facing catalogue Features (analytics, loyalty, gift
> cards, auto-admin, …) and step-by-step usage, see the authoritative
> `docs/usage/` (Persian).

## Public registration & onboarding
- **What it is:** the front door — a merchant signs up and confirms their email.
- **Why we need it:** self-service means no operator has to create accounts by hand.
- **How to use it:** go to `/signup`, enter name/email/password, then click the
  verification link (sent by email, or read from the server log when email isn't
  configured), then sign in.

## Plans & the catalogue
- **What it is:** the menu of what's for sale — plans that bundle abilities, priced.
- **Why we need it:** merchants need clear packages, and billing needs to know what
  each plan grants.
- **How to use it:** on `/portal/plans`, pick a plan (and any optional abilities),
  see the total in Toman, and continue to payment.

## Checkout, billing & subscriptions
- **What it is:** taking payment and turning it into an active subscription.
- **Why we need it:** payment is what unlocks everything downstream, automatically.
- **How to use it:** complete checkout; on success your subscription becomes active
  and your abilities are granted — no further steps.

## Entitlements
- **What it is:** the record of which abilities a customer is allowed to have.
- **Why we need it:** "allowed to have" and "actually installed" are different facts;
  keeping them separate is what makes upgrades and downgrades safe.
- **How to use it:** it's automatic — buying a plan grants its entitlements; the
  store install step reads them.

## Provisioning
- **What it is:** the moving-in crew that stands up a new store after payment.
- **Why we need it:** a paid signup must become a running store with no human step.
- **How to use it:** nothing to do — watch the progress bar in the portal; it walks
  server → agent → credential → base image → optional-feature install → health.

## Feature registry & delivery
- **What it is:** the library of versioned abilities and the machinery that installs
  them into the right stores.
- **Why we need it:** an ability is built once and delivered everywhere it's paid
  for — never hand-copied per store.
- **How to use it:** operators publish a Feature version; delivery installs/upgrades
  it on entitled stores (base abilities ship in the store image and aren't delivered).

## Stores, servers & agents
- **What it is:** the registry of real stores, the machines they run on, and the
  agents that carry out work on those machines.
- **Why we need it:** the control plane must know where each store lives and be able
  to act on it safely.
- **How to use it:** operators see and manage these in the dashboard; merchants just
  see their own store's status.

## Observability (ingestion, logs, alerts, incidents)
- **What it is:** the health monitor — stores report health, logs and events; KNIGHT
  raises alerts and incidents.
- **Why we need it:** with many independent stores, you can't watch them by hand.
- **How to use it:** operators read alerts/incidents/logs in the dashboard; a
  delivery that fails or a store that goes quiet surfaces on the screen they already
  watch.

## Access control & audit
- **What it is:** who can sign in, what they're allowed to do, and a record of what
  they did.
- **Why we need it:** platform staff and merchants are different principals with
  different powers, and actions must be traceable.
- **How to use it:** operators manage users/roles in the Access screen; the audit log
  answers "who did this?".

## Automatic Admin
- **What it is:** an assistant that generates and publishes content to a store's
  channels (e.g. Telegram) on autopilot.
- **Why we need it:** small merchants can't run a content team; this automates it.
- **How to use it:** open `/portal/auto-admin`, pick an autonomy mode, add the
  channel keys/tokens, and let it run. (Going fully live needs owner-supplied AI and
  channel credentials.)

## Traceability matrix

| Feature | Guide section | Code location | Key API(s) | Status |
|---|---|---|---|---|
| Public registration | #public-registration--onboarding | `backend/modules/Onboarding`, `Api/.../ControlPlaneAuthEndpoints.cs`, `Infrastructure/.../Integration/VerificationEmailSender.cs` | `POST /auth/register`, `/auth/verify-email` | built (email = log fallback until SMTP) |
| Plans & catalogue | #plans--the-catalogue | `backend/modules/Plans`, `Infrastructure/.../Seed/CommercialCatalogueSeeder.cs` | `GET /catalog/plans` | built |
| Checkout & billing | #checkout-billing--subscriptions | `backend/modules/PlatformBilling`, `Billing`, `Subscriptions`; `ControlPlaneBillingEndpoints.cs` | `POST /billing/checkout`, `/billing/webhooks/simulated` | built (payment simulated) |
| Entitlements | #entitlements | `backend/modules/Subscriptions` (FeatureEntitlement) | via `/me/*`, delivery | built |
| Provisioning | #provisioning | `backend/modules/Provisioning`, `Infrastructure/.../Adapters/ProvisioningAdapters.cs`, `Api/BackgroundServices/SimulatedInfrastructureWorker.cs` | `GET /me/stores/{id}/provisioning` | built (infra simulated on `.local`) |
| Feature registry & delivery | #feature-registry--delivery | `backend/modules/FeatureRegistry`, `FeatureDelivery`; `ControlPlaneDeliveryEndpoints.cs` | delivery/install endpoints | built |
| Stores/servers/agents | #stores-servers--agents | `backend/modules/Stores`, `Servers`; `ControlPlaneStoreEndpoints.cs`, `ServerEndpoints.cs`, `AgentEndpoints.cs` | store/server/agent endpoints | built |
| Observability | #observability-ingestion-logs-alerts-incidents | `backend/modules/Observability`, `Ingestion`; `ControlPlaneObservabilityEndpoints.cs`, `LogEndpoints.cs` | observability/logs/alerts | built |
| Access & audit | #access-control--audit | `backend/modules/AccessControl`; `ControlPlaneAccessEndpoints.cs`, `AuditLogEndpoints.cs` | access/audit endpoints | built |
| Automatic Admin | #automatic-admin | `backend/modules/AutoAdmin`; `ControlPlaneAutoAdminEndpoints.cs`; portal `PortalAutoAdminPage.tsx` | auto-admin endpoints | engine built; AI keys/channel tokens pending |
| Customer portal (UI) | (portal) | `frontend/knight-dashboard/src/features/portal/*` | `/me/*`, `/catalog/*`, `/billing/*` | built |
