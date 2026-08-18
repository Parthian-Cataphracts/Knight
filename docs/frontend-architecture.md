# Frontend Architecture — KNIGHT Dashboard

Status: **authoritative**. See [`adr/0011`](adr/0011-react-vite-dashboard.md).
Supersedes the Next.js/storefront layout described in `frontend/README.md`.

The KNIGHT frontend is **one** application: an administrative control
dashboard. Customer storefronts and store admin panels belong to the
independent Django stores and are **not** part of this repository.

## 1. Stack

| Concern | Choice |
|---|---|
| Build | Vite |
| Language | React 19 + TypeScript (strict) |
| Routing | React Router (data router, lazy routes) |
| Server state | TanStack Query |
| Client state | Zustand — only for auth session, theme, sidebar; nothing else |
| Forms | React Hook Form + Zod (schemas shared with API contract types) |
| Styling | Tailwind CSS with logical properties |
| Components | shadcn/ui (Radix primitives) |
| Charts | Recharts |
| Tables | TanStack Table |
| Real-time | `@microsoft/signalr` |
| i18n | i18next — Persian (default, RTL) and English (LTR) |
| Testing | Vitest + Testing Library; Playwright for critical flows |
| Quality | ESLint, Prettier, `tsc --noEmit` in CI |

## 2. Directory layout

```
frontend/knight-dashboard/
├── index.html
├── src/
│   ├── app/                 router, providers, error boundaries, app shell
│   ├── features/
│   │   ├── auth/            login, session, permission guards
│   │   ├── dashboard/       overview tiles and charts
│   │   ├── customers/
│   │   ├── stores/
│   │   ├── plans/
│   │   ├── features/        registry: features, versions, manifests, publishing
│   │   ├── installations/   per-store feature state, jobs, live progress
│   │   ├── subscriptions/
│   │   ├── billing/
│   │   ├── servers/
│   │   ├── monitoring/
│   │   ├── errors/
│   │   ├── logs/
│   │   ├── incidents/
│   │   ├── reports/
│   │   ├── users/
│   │   ├── roles/
│   │   ├── audit/
│   │   └── settings/
│   ├── components/          shared presentational components (ui/, data/, feedback/)
│   ├── layouts/             AppLayout, AuthLayout
│   ├── hooks/               cross-feature hooks
│   ├── lib/                 api client, query client, signalr, formatters, rtl helpers
│   ├── types/               generated + hand-written API types
│   └── i18n/                fa.json, en.json
└── tests/
```

Each feature folder owns `api/` (query/mutation hooks), `components/`,
`pages/`, and `types.ts`. Nothing outside a feature imports its internals
except its exported pages and hooks.

## 3. Rules

- No business logic in components. Pricing, entitlement, and permission
  decisions come from the API; the UI only renders and requests.
- No global state for anything the server owns — TanStack Query is the cache.
- One API client (`lib/api/client.ts`) handles base URL, auth header, refresh
  on 401, correlation id, and ProblemDetails parsing. No `fetch` in components.
- Types mirror `Knight.Contracts`; generate them from OpenAPI where possible.
- Route-level code splitting for every feature.
- Permission-aware navigation: menu items and actions render from
  `/api/v1/auth/me` permissions — **as a convenience, never as security**.
- Long-running operations (installs, upgrades, provisioning) are modelled as
  **jobs**, not as request/response spinners: the UI creates a job, then follows
  it over SignalR with polling as a fallback, and survives a page reload.

## 4. RTL and internationalisation

Persian is the default locale and the default direction is RTL.

- `<html dir="rtl" lang="fa">`, switched at runtime by the locale store.
- Tailwind **logical properties only**: `ms-*`/`me-*`/`ps-*`/`pe-*`,
  `start-*`/`end-*`, `text-start`/`text-end`. `ml-*`, `pr-*`, `left-*`,
  `right-*` are forbidden and blocked by an ESLint rule.
- Directional icons (chevrons, arrows, back buttons) mirror with
  `rtl:-scale-x-100`; logos, media, and charts do not mirror.
- Numbers, dates, and currency go through `Intl` formatters with the active
  locale; Jalali calendar display is supported for `fa`.
- Charts flip axis orientation and legend placement per direction.
- Mixed-direction content (domains, versions, stack traces, log lines, code) is
  wrapped in `dir="ltr"` with `unicode-bidi: isolate`.
- Every screen is verified in both `fa`/RTL and `en`/LTR before it is done.

## 5. Responsive design (mobile and desktop are both first-class)

Breakpoints: `sm 640 · md 768 · lg 1024 · xl 1280 · 2xl 1536`.

| Viewport | Shell | Data presentation |
|---|---|---|
| `< md` | top bar + off-canvas drawer nav, bottom-safe-area padding | tables collapse to stacked cards; filters in a bottom sheet; charts simplified with fewer ticks |
| `md–lg` | collapsible icon rail | condensed tables, horizontal scroll containers |
| `≥ lg` | persistent sidebar, sticky page header | full tables with sorting/pagination, multi-column detail layouts, side drawers instead of full-page dialogs |

Rules: touch targets ≥ 44px; no horizontal page scroll at any width; wide
tables scroll inside their own container; dialogs become full-screen sheets on
mobile; the dashboard is designed mobile-first and enhanced upward.

## 6. Accessibility

WCAG 2.1 AA as the baseline: keyboard operability everywhere, visible focus
rings, correct roles/labels via Radix, live regions for async status, contrast
checked in both light and dark themes, and `prefers-reduced-motion` respected.

## 7. Theming

CSS custom properties for the palette, light/dark support driven by
`data-theme` with a system default. A Persian-friendly typeface (e.g. Vazirmatn)
is self-hosted; no external font CDN.

## 8. Design source

Visual design is being explored in Stitch
(`https://stitch.withgoogle.com/projects/10363262931731977567`). Stitch output
is treated as a **visual reference only** — generated markup is not copied into
the codebase; components are implemented against the design system above.

## 9. Feature delivery in the UI

Two ideas must never be shown as one toggle:

```
Entitlement   commercial   "purchased"        → subscription screens
Installation  technical    "installed 1.4.0, healthy" → store screens
```

Required surfaces:

- **Feature registry**: features, their versions, manifest view, publish/yank
  with a clear warning that publishing ships code, and a "which stores are
  affected" preview.
- **Store → Features tab**: each feature with entitlement state, installed
  version, installation state badge, health, and available actions (install,
  upgrade, configure, disable, uninstall, rollback).
- **Install preview dialog**: the resolved dependency plan, compatibility
  verdict, whether migrations run, declared reversibility and estimated
  duration — shown *before* confirmation. Irreversible operations require an
  explicit typed confirmation.
- **Job progress**: step list with per-step status and live progress
  (`Migration 2/4`), elapsed time, scrubbed output tail, and the failure reason
  plus rollback outcome when it fails.
- **Failure visibility**: an entitled-but-failed feature is surfaced on the
  dashboard overview, never buried in a detail tab.

## 10. Definition of done for a screen

Implemented · typed · loading/empty/error states · RTL and LTR verified ·
mobile and desktop verified · permission-aware · tested (component test, plus
a Playwright flow if critical) · documented in the feature folder.
