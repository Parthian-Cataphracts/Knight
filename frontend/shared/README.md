# Shared

Code genuinely shared between multiple independently deployed tenant frontend
projects. This directory intentionally stays empty until a second tenant
frontend exists and a real sharing need is identified — nothing here is
populated speculatively.

- `admin-core/` — shared building blocks for tenant admin frontends
- `storefront-core/` — shared building blocks for tenant storefront frontends
- `api-client/` — generated or hand-written client for the platform API
- `contracts/` — frontend-side mirrors of API request/response shapes
- `types/` — shared TypeScript types
- `config/` — shared lint/build/tooling configuration

Backend domain code must never live here.
