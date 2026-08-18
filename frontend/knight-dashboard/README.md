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

Implemented: design tokens, RTL and theme switching, app shell, login,
dashboard overview.

Remaining screens are listed under phase 6 in [`TODO.md`](../../TODO.md). Routes
for them exist and render a placeholder so navigation is complete.
