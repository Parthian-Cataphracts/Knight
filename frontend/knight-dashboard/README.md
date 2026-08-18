# Knight Dashboard

The KNIGHT administrative dashboard. React 19 + Vite + TypeScript, Persian-first
(RTL), responsive from phone to desktop.

Specification: [`docs/frontend-architecture.md`](../../docs/frontend-architecture.md).
Visual system: [`docs/design-system.md`](../../docs/design-system.md) (Aegis Command).
API contract: [`docs/api-contracts.md`](../../docs/api-contracts.md).

## Running

```bash
npm install
```

```bash
cp .env.example .env
```

```bash
npm run dev
```

The KNIGHT API does not exist yet (see [`TODO.md`](../../TODO.md) phase 1), so
`VITE_USE_MOCKS=true` serves fixtures from `src/lib/api/mock.ts` that follow the
documented contract. Set it to `false` and point `VITE_API_BASE_URL` at a real
API to switch over — no other code changes.

Any credentials with a password of four characters or more sign in against the
fixtures; a shorter password exercises the `401` path.

## Signing in

Fixture mode (`VITE_USE_MOCKS=true`, the default) accepts a single seeded
account:

| Field | Value |
|---|---|
| Email | `admin@knight.local` |
| Password | any 4+ characters, e.g. `devpassword` |
| MFA code | any 6 digits, e.g. `123456` |

It signs in as a platform `SuperAdmin`, so every screen and action is visible.
A password shorter than four characters exercises the `401` path.

The control-plane auth API does not exist yet (TODO.md phase 1), so
`VITE_USE_MOCKS=false` has nothing to talk to. The `Knight.Bootstrap` tool in
`backend/tools` creates an administrator, but against the *legacy* schema and
endpoints, which this dashboard does not use.

## Scripts

| Command | Purpose |
|---|---|
| `npm run dev` | Vite dev server on port 5173 |
| `npm run build` | Type-check (`tsc --noEmit`) then production build |
| `npm run preview` | Serve the production build |
| `npm test` | Vitest |

## Structure

```
src/
├── app/          router, providers, auth gate
├── layouts/      app shell (sidebar / rail / drawer), navigation model
├── features/     one folder per area: auth, dashboard, …
├── components/ui shared primitives (Card, Button, TextField, StatusChip, Meter, state blocks)
├── lib/          api client, query client, fixtures, formatters
├── store/        session and UI state (Zustand)
├── i18n/         fa (default, RTL) and en (LTR)
└── styles/       Aegis Command tokens
```

## Conventions

- No `fetch` outside `src/lib/api`. Server state lives in TanStack Query.
- **Logical properties only** — `ms-*`, `me-*`, `ps-*`, `pe-*`, `start-*`,
  `end-*`. Never `ml-*`, `pr-*`, `left-*`, `right-*`.
- Directional icons mirror with `rtl:-scale-x-100`; semantic icons do not.
- Identifiers, versions, domains and latencies render inside `dir="ltr"`.
- Permissions from `/auth/me` decide what the UI *offers*; the API decides what
  is *allowed*.
- Every screen: loading, empty and error states; verified in fa/RTL and en/LTR,
  on mobile and desktop.

## Status

Every screen in the design is implemented and reads through the API client:
Dashboard, Customers (list, detail, create), Stores (list, detail with domains,
credentials and deployments), Features registry, Installations & Jobs with
install preview, Plans & Subscriptions, Billing, Infrastructure, Monitoring,
Alerts, Errors with event samples, Incidents with timeline, Logs, Reports,
Users & Access, Audit log and Settings. Sign-in includes the second-factor step.

Every screen is verified in fa/RTL and en/LTR, at mobile and desktop widths,
with no horizontal page scroll.

What remains needs the API rather than more UI: persisting form submissions,
the subscription change flow with live pricing from `/subscriptions/quote`, and
live job progress over SignalR instead of a one-shot fetch. Test suites are
also still to be written. Tracked under phase 6 in [`TODO.md`](../../TODO.md).
