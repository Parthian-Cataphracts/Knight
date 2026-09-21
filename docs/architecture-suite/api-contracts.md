# KNIGHT — Core API contracts (generated summary)

> Generated from the endpoint files in `Knight.Api/`. Shapes are representative,
> not exhaustive — the authoritative contract is the parent `docs/api-contracts.md`
> and the running OpenAPI. All errors use one envelope:
> `{ "error": { "errorCode": "...", "message": "..." } }` (note: `errorCode`, not
> `code` — see the project memory on the error contract).

Base path: `/api/v1`. Self-service endpoints are anonymous; `/me/*` needs a
customer bearer token; operator endpoints need a platform token.

### POST /auth/register — start self-service signup
```
Request:  { "email": "a@shop.com", "password": "•••", "name": "Ali", "companyName": "Shop" }
Response 202: (no body) — account created, verification pending
```

### POST /auth/verify-email — confirm the address
```
Request:  { "token": "OIsVIqwL…" }
Response 200: (no body) — account active, may sign in
```

### GET /catalog/plans — public plan catalogue
```
Response 200:
[ { "id":"uuid", "key":"basic", "name":"Basic", "basePrice":490000, "currency":"IRT",
    "includedFeatures":[{ "featureId":"uuid","slug":"storefront","name":"Storefront" }],
    "optionalFeatures":[{ "featureId":"uuid","slug":"ai-recommendations","price":190000,"currency":"IRT" }] } ]
```

### POST /billing/checkout — start checkout for a plan (+ optional features)
```
Request:  { "planId":"uuid", "billingInterval":"Monthly", "selectedFeatureIds":["uuid"] }
Response 200: { "checkoutSessionId":"uuid","subscriptionId":"uuid",
                "checkoutUrl":"https://…/portal/pay?session=sim_sess_…","amount":490000,"currency":"IRT" }
```

### POST /billing/webhooks/simulated — settle payment (test gateway)
```
Request:  { "type":"payment_succeeded","providerSessionId":"sim_sess_…","providerTransactionId":"sim_tx_…" }
Response 200: (no body) — activates the subscription, grants entitlements, queues provisioning
```

### GET /me/stores — the signed-in merchant's stores
```
Response 200:
[ { "id":"uuid","name":"Basic store","primaryDomain":"store-….stores.knight.local",
    "status":"active","integrationStatus":"Connected","isReady":true } ]
```

### GET /me/stores/{storeId}/provisioning — live provisioning status
```
Response 200:
{ "storeId":"uuid","state":"ready","friendlyStatus":"…","percentComplete":100,
  "steps":[{ "name":"base-features","status":"succeeded" }] }
```

### GET /health/ready — liveness/readiness (anonymous)
```
Response 200: healthy · 503: degraded
```

> Operator-side groups (not detailed here) include customers, stores, plans,
> subscriptions, feature registry/delivery, provisioning, servers, images,
> rollouts, billing, observability, insights, logs, access, audit — plus the
> agent/artifact/ingest endpoints stores call inward.
