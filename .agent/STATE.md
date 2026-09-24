# KNIGHT — project state (north star)

> The cold-start brief. Any agent/tool reads this first. Authoritative detail lives
> in `TODO.md`, `docs/README.md`, and `docs/architecture-suite/`.

## What this is
KNIGHT is a central **control plane** (ASP.NET Core modular monolith + React
dashboard, PostgreSQL) that registers merchants, bills them, provisions their
independent stores, observes them, and delivers versioned **Features** into them.
Moving to **self-service SaaS**: register → pay → auto-provisioned store.

## Current goal / phase
Self-service journey is **complete and walkable end-to-end** as a demo (register →
verify → login → plan → pay → store Active), Persian-first, priced in Toman, with
in-product help. Live at `knight.abolfazltafakori.com`. BojanStore is the first
real store (`bojanstore.com`).

## What's live vs. still owner-gated
- Live/working: full portal, Persian UI, Toman prices, provisioning (simulated
  infra on `.local`), in-product help, evidence-verified.
- Open (need owner decision/credentials): real store hosting for arbitrary signups
  (wildcard DNS/TLS + per-store deploy), real payment gateway, real SMTP,
  Automatic-Admin AI keys + channel tokens, security review, backup custody.

## How to run / verify
- Deploy: push to `main`, then `knightctl update` on the server (167.233.37.47).
- Dashboard build (Windows): see memory `knight-dashboard-prod-build-api-base`
  (`MSYS_NO_PATHCONV=1 … VITE_API_BASE_URL=/api/v1 npm run build`).
- Tests: `dotnet test tests/Knight.UnitTests` (795 pass); full suite needs Postgres.

## Rules
`CLAUDE.md` is authoritative (no AI traces in commits, commit+push continuously,
verify with real output before claiming done).
