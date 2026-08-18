# 0011 — React + Vite + TypeScript for the KNIGHT dashboard

- Status: **Accepted**
- Date: 2026-08-18
- Supersedes: 0003 (independent tenant frontends)

## Context

`frontend/` is empty apart from a README describing a planned Next.js layout
with per-tenant storefronts and admin panels. After [`0010`](0010-pivot-to-control-plane.md),
storefronts belong to the independent Django stores and are not part of this
repository. What KNIGHT needs is a single administrative dashboard: an
authenticated, data-dense, real-time internal tool — Persian-first (RTL) and
responsive on both mobile and desktop.

## Options considered

1. **Next.js** — SSR, SEO, file-system routing. None of these matter for an
   authenticated internal dashboard; SSR adds a Node runtime to deploy and
   complicates token handling.
2. **React + Vite SPA** — static bundle, fast HMR, trivial deployment behind the
   existing reverse proxy, no server runtime.

## Decision

**React 19 + Vite + TypeScript (strict)**, deployed as a static bundle.
Ecosystem: React Router (data router), TanStack Query for server state, Zustand
only for session/theme/sidebar, Tailwind CSS with logical properties,
shadcn/ui + Radix, TanStack Table, Recharts, `@microsoft/signalr`, i18next with
Persian default and RTL, Vitest + Testing Library + Playwright.

One application at `frontend/knight-dashboard/`. The `tenants/` and
`storefront-core/` placeholders are removed.

## Consequences

**Positive** — simplest deployment for a purely authenticated UI; no SSR/token
complexity; fast local iteration; full control over RTL and responsive
behaviour; server state handled by one well-understood cache.

**Negative** — no SSR should marketing pages ever be needed (they would be a
separate project anyway); route-level code splitting must be deliberate to keep
the initial bundle small.

**Constraint** — RTL is not a theme switch: logical properties are mandatory and
enforced by lint; every screen is verified in `fa`/RTL and `en`/LTR, on mobile
and desktop, before it is considered done.
