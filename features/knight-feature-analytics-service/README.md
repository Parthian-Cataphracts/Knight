# knight-feature-analytics-service

Analytics delivered as an **external service** (`architecture: external_service`)
rather than an in-process package. It carries the same slug as the Django
package — `analytics-core` — at a major-bumped version (`2.0.0`), the same way
`knight-feature-subscriptions-service` supersedes `knight-feature-subscriptions`.

The point: a store never loads this code. It forwards its lifecycle events over
HTTP and proxies the merchant's analytics screen through, so a store of **any**
runtime — a .NET shop like BojanStore, a Django shop — can use it. Write once,
run once, connect from anywhere.

## Layout

- `knight_manifest.yaml` — the published contract (events forwarded, routes
  proxied, the shared-secret name, the service base URL).
- `service/` — the service itself: a small FastAPI app that verifies the store's
  HMAC signature, records events into SQLite partitioned by store, and serves the
  merchant's dashboard.
- `deploy/` — a compose file for running it on the operator's host behind nginx.

## The two secrets it verifies

One signing scheme, HMAC-SHA256 over

```
METHOD \n path \n timestamp \n nonce \n sha256hex(body)
```

carried as `X-Knight-Signature: sha256=<hex>` with `X-Knight-Timestamp` and
`X-Knight-Nonce`. Two callers, two keys:

- **KNIGHT → the control routes** (`/knight/stores/register|rotate|revoke`) are
  signed with the per-Feature **control secret**, shared with the control plane's
  `ServiceControlPlane:Secrets:analytics-core` and set on both sides out of band.
  These calls hand the service each store's own signing secret (adr/0034).
- **A store → its webhooks and proxied requests** are signed with the **store
  secret** delivered over those control calls, looked up by `X-Knight-Store`.

Anything outside the skew window, replayed, or signed with the wrong key is
refused. `/healthz` is the one unauthenticated route.

## Deploying

```bash
# on the host that operates the service
cd deploy
echo "ANALYTICS_CONTROL_SECRET=<the control secret>" > .env
docker compose up -d --build
```

Then point `https://analytics.<domain>` at it in nginx (TLS via certbot) so the
published `base_url` resolves, and set the same value as the control plane's
`ServiceControlPlane__Secrets__analytics-core`. Store secrets are not configured
here — KNIGHT issues them and registers them over the control routes.

## Publishing and installing

```bash
KNIGHT_SIGNING_KEY=<prod PKCS8 b64> KNIGHT_SIGNING_KEY_ID=prod \
KNIGHT_TOKEN=<admin token> KNIGHT_BASE_URL=https://knight.<domain> \
KNIGHT_ARTIFACT_ROOT=/opt/knight/artifacts \
  python features/tools/knight_package.py publish features/knight-feature-analytics-service

# then install analytics-core 2.x onto the store from the dashboard, or:
# POST /api/v1/installations/install { storeId, slug: "analytics-core", versionRange: ">=2.0.0" }
```
