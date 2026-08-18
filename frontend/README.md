# Frontend

The KNIGHT administrative dashboard lives here — **one** application, not a set
of per-tenant frontends.

```
knight-dashboard/     React 19 + Vite + TypeScript (to be created — TODO Phase 6)
```

Customer storefronts and store admin panels are **not** part of this
repository. Each store is an independent Django application and owns its own
UI. The previous plan (Next.js `super-admin/`, `tenants/<slug>/storefront|admin`,
`shared/*`) is discarded by
[`../docs/adr/0011`](../docs/adr/0011-react-vite-dashboard.md).

## Specification

Read [`../docs/frontend-architecture.md`](../docs/frontend-architecture.md)
before writing any code. Summary:

- React Router · TanStack Query for server state · Zustand only for
  session/theme/sidebar · Tailwind (logical properties only) · shadcn/ui ·
  TanStack Table · Recharts · SignalR · i18next
- **RTL-first**: Persian is the default locale; `ms-*`/`me-*`/`ps-*`/`pe-*` and
  `start`/`end` only — `ml-*`, `pr-*`, `left-*`, `right-*` are lint errors
- **Responsive**: mobile-first, with a designed experience for phone, tablet,
  and desktop — not a shrunken desktop layout
- Feature-folder structure under `src/features/*`
- No business logic in components; pricing, entitlement, and permission
  decisions come from the API
- Every screen: loading/empty/error states, RTL + LTR verified, mobile +
  desktop verified, permission-aware, tested
- Feature screens must keep **entitlement** (purchased) and **installation**
  (deployed, versioned, healthy) visually distinct, and follow long-running
  installs as live jobs — see
  [`../docs/feature-delivery.md`](../docs/feature-delivery.md)

## API

Contracts: [`../docs/api-contracts.md`](../docs/api-contracts.md). With the
backend running in Development, OpenAPI is at `/openapi/v1.json` and an
interactive reference at `/scalar`. Types are generated from OpenAPI rather
than hand-copied.
