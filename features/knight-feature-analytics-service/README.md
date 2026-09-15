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

## The signature it verifies

Every request except `/healthz` is signed by the store's ServiceProxy with the
shared secret, HMAC-SHA256 over

```
METHOD \n path \n timestamp \n nonce \n sha256hex(body)
```

arriving as `X-Knight-Signature: sha256=<hex>`, with `X-Knight-Timestamp`,
`X-Knight-Nonce`, `X-Knight-Store` and the identity headers. A request outside
the skew window, replayed, or signed with another key is refused.

## Deploying

```bash
# on the host that operates the service
cd deploy
echo "ANALYTICS_SERVICE_SECRET=<the shared secret>" > .env
docker compose up -d --build
```

Then point `https://analytics.<domain>` at it in nginx (TLS via certbot) so the
published `base_url` resolves, and set the same `ANALYTICS_SERVICE_SECRET` as the
store's delivered feature secret.

## Publishing and installing

```bash
KNIGHT_SIGNING_KEY=<prod PKCS8 b64> KNIGHT_SIGNING_KEY_ID=prod \
KNIGHT_TOKEN=<admin token> KNIGHT_BASE_URL=https://knight.<domain> \
KNIGHT_ARTIFACT_ROOT=/opt/knight/artifacts \
  python features/tools/knight_package.py publish features/knight-feature-analytics-service

# then install analytics-core 2.x onto the store from the dashboard, or:
# POST /api/v1/installations/install { storeId, slug: "analytics-core", versionRange: ">=2.0.0" }
```
