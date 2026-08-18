# Frontend

All frontend work for the platform lives here. Each application is an
independently designed and independently deployable Next.js/TypeScript
project consuming the shared central API in `../backend`.

## Layout

```
super-admin/        the Super Admin frontend (platform-level administration)

tenants/
  <slug>/
    storefront/     the tenant's public storefront
    admin/          the tenant's administration interface

shared/
  admin-core/       shared building blocks for tenant admin frontends
  storefront-core/  shared building blocks for tenant storefront frontends
  api-client/       client for the platform API
  contracts/        frontend-side mirrors of API request/response shapes
  types/            shared TypeScript types
  config/           shared lint/build/tooling configuration
```

## Principles

- Every tenant frontend is designed on its own terms. No tenant's frontend is
  a template or an example for another — see
  [`docs/adr/0003-independent-tenant-frontends.md`](../docs/adr/0003-independent-tenant-frontends.md).
- `shared/` stays empty until a second frontend exists and a real sharing need
  is identified. Nothing is placed there speculatively, and nothing
  tenant-specific belongs there.
- Backend domain code never lives here. Business rules, feature access, and
  permissions are owned by the API; frontends never duplicate that logic.

## API

The API contract is documented in
[`docs/api/README.md`](../docs/api/README.md). With the backend running in the
Development environment, the OpenAPI document is served at `/openapi/v1.json`
and an interactive reference at `/scalar`.
