# KNIGHT Documentation Index

> **Read this file first.** It tells you what KNIGHT is, what state the
> repository is actually in, and which documents are authoritative.

## What KNIGHT is

KNIGHT is a **central control plane** operated by a web-design company to
manage many **independent customer stores**. Each customer store is its own
Django application, with its own domain, its own database, and its own
deployment. KNIGHT manages, observes, bills, configures — and **delivers
software to** — those stores. KNIGHT is **never** the business backend of a
store.

A **Feature** is versioned, deployable Django functionality: implemented once,
packaged, registered in KNIGHT, and installed automatically into every store
whose customer is entitled to it. It is **not** a boolean flag, and it is never
re-implemented or hand-copied per customer
([`feature-delivery.md`](feature-delivery.md)).

```
                         KNIGHT (ASP.NET Core + React dashboard)
                                       |
              +------------------------+------------------------+
              v                        v                        v
         Store A (Django)         Store B (Django)        Store C (Django)
          cafe1.ir                 cafe2.ir                cafe3.ir
          Server A                 Server A (shared)       Dedicated server
```

## Current repository state (2026-08-20)

The pivot to the control-plane specification
([`adr/0010`](adr/0010-pivot-to-control-plane.md)) is landed. Phase 8 ported the
store-side business domains to Django and deleted their .NET counterparts, so
the two realities that used to sit side by side are now one: this solution is a
control plane and nothing else, and an architecture test fails the build if a
store business domain reappears in it.

| | |
|---|---|
| **Control plane** | Customers, stores, access control and auditing, plans, subscriptions, entitlements, billing; store ingestion, health polling and domain verification; the Feature registry and delivery pipeline; servers, agents and monitoring; errors, incidents and notifications |
| **Stores** | [`stores/reference-store/`](../stores/reference-store/README.md) — a real Django store with the full `knight_integration` layer and the ported business domains, each store on its own database |
| **Features** | [`features/`](../features/) — installable Django packages. What is sold, and what is base, is [`feature-catalog.md`](feature-catalog.md) ([`adr/0024`](adr/0024-base-store-versus-optional-feature.md)) |
| **Dashboard** | `frontend/knight-dashboard/` — every screen against the real API |
| **Not built yet** | An **external security review of the code-delivery path** ([`security/external-review-scope.md`](security/external-review-scope.md)), and the container/registry half of the pipeline a hosting-platform decision unblocks — Docker images and deploy stages. Deploying to a server is done and does not wait on that decision: [`installation.md`](installation.md) installs a whole deployment, nightly backup included. Provisioning, outbound email and the phase 10 hardening are done ([`phase-9-verification.md`](phase-9-verification.md), [`phase-10-verification.md`](phase-10-verification.md)) |

Detailed inventory: [`current-state-analysis.md`](current-state-analysis.md).

## Authoritative documents (target architecture)

Read in this order:

1. [`current-state-analysis.md`](current-state-analysis.md) — what is really in the repo today, file by file
2. [`architecture.md`](architecture.md) — target system architecture, containers, modules, boundaries
3. [`feature-delivery.md`](feature-delivery.md) — **the Feature registry, packaging, and installation pipeline** (read before anything about features)
   — and [`feature-catalog.md`](feature-catalog.md) for **what is actually sold**:
   the three tiers, the base-store boundary, the dependency graph, and the
   procedure for adding a Feature
4. [`domain-model.md`](domain-model.md) — control-plane entities and relationships
5. [`api-contracts.md`](api-contracts.md) — Dashboard↔KNIGHT, KNIGHT↔Store, Agent↔KNIGHT contracts
6. [`store-integration.md`](store-integration.md) — the Django integration layer and its lifecycle
   (implemented in [`stores/reference-store/`](../stores/reference-store/README.md);
   the wire contract both sides test against is
   [`contracts/store-integration.schema.json`](contracts/store-integration.schema.json))
   — and [`connecting-a-store.md`](connecting-a-store.md) for **the same integration
   without a framework**: what a store of any stack must call and serve, the two signed
   strings, and the conformance checker it is finished against
7. [`store-provisioning.md`](store-provisioning.md) — from customer signup to a ready store
8. [`authentication.md`](authentication.md) — human auth, store auth, agent auth
9. [`authorization.md`](authorization.md) — roles, permissions, customer isolation
10. [`frontend-architecture.md`](frontend-architecture.md) — React + Vite + TS dashboard, RTL, responsive
11. [`observability.md`](observability.md) — logs, metrics, traces, correlation, errors, delivery visibility
12. [`deployment.md`](deployment.md) — environments, feature delivery pipeline, config, secrets
13. [`installation.md`](installation.md) — the one-command server install: what it creates, and how it shares a machine with other applications
14. [`security-threat-model.md`](security-threat-model.md) — threats and required controls
15. [`migration-plan.md`](migration-plan.md) — how to get from the current repo to the target
16. [`risks.md`](risks.md) — open risks, contradictions, unresolved decisions
17. [`roadmap.md`](roadmap.md) — the whole remaining trajectory: seven phases to production, every open item classified, and the decisions that are the product owner's
18. [`phase-23-verification.md`](phase-23-verification.md) — the live service layer, and the six defects that only two processes disagreeing could show
17. [`development.md`](development.md) — how to run, test, and contribute
18. [`phase-3-verification.md`](phase-3-verification.md) — the exact steps to bring KNIGHT, a store and the dashboard up together and see the link work
19. [`phase-11-verification.md`](phase-11-verification.md) — the exact steps to install KNIGHT on a server and prove it did not disturb anything else on it
20. [`phase-12-verification.md`](phase-12-verification.md) — moving two capabilities out of Features and into the base store, what it cost, and what running it found
21. [`phase-13-verification.md`](phase-13-verification.md) — three Features through the delivery engine, and the runtime wiring it turned out never to send
22. [`phase-14-verification.md`](phase-14-verification.md) — the two Features that keep a balance, and the two transaction bugs that only a real database shows
23. [`phase-15-verification.md`](phase-15-verification.md) — scheduled work, the first third-party credential, and a spend cap that refuses before it costs anything
24. [`phase-16-verification.md`](phase-16-verification.md) — the operational Features, and the migration risk `multi-location` was held back for turning out never to have existed
25. [`phase-17-verification.md`](phase-17-verification.md) — the last two Features, and a store that is not Django taking delivery of one
26. [`phase-18-verification.md`](phase-18-verification.md) — the catalogue installed through the delivery path for the first time, and the eight defects that had made it impossible
27. [`phase-19-verification.md`](phase-19-verification.md) — that journey turned into one command CI runs on every push, and the ninth defect it found: a rollback that reversed the code and not the schema

Project status and remaining work: [`../TODO.md`](../TODO.md).

## Legacy documents (previous product — NOT the target)

These describe the shared multi-tenant food-service SaaS. They remain useful
for understanding the existing code that the pivot has to migrate or retire,
but they are **not** a description of KNIGHT's target architecture:

- `architecture/platform-overview.md`
- `architecture/multi-tenancy.md`
- `architecture/authorization.md`
- `architecture/repository-structure.md`
- `architecture/catalog.md`
- `architecture/customer.md`
- `architecture/fulfillment-and-delivery.md`
- `api/README.md`, `database/README.md`, `security/README.md`
- `adr/0001` … `adr/0009` (superseded in part by `adr/0010`)

## Architecture Decision Records

| ADR | Title | Status |
|---|---|---|
| 0001–0009 | Previous product decisions | Partly superseded by 0010 |
| [0010](adr/0010-pivot-to-control-plane.md) | Pivot from shared multi-tenant SaaS to control plane | Accepted |
| [0011](adr/0011-react-vite-dashboard.md) | React + Vite + TypeScript for the KNIGHT dashboard | Accepted |
| [0012](adr/0012-store-authentication-mechanism.md) | Store authentication via rotatable credentials + short-lived tokens | Proposed |
| [0013](adr/0013-error-grouping-strategy.md) | Error fingerprinting and grouping strategy | Proposed |
| [0014](adr/0014-features-as-deployable-packages.md) | **Features are versioned deployable Django packages, not flags** | Accepted |
| [0015](adr/0015-feature-delivery-mechanism.md) | Delivery via agent-pulled typed jobs and signed artifacts | Accepted |
| [0016](adr/0016-feature-migration-and-removal-policy.md) | Feature migration, rollback, and removal policy | Accepted |
| [0017](adr/0017-feature-compatibility-and-dependencies.md) | Feature versioning, compatibility, dependency resolution | Accepted |
| [0018](adr/0018-separate-control-plane-context-and-access-module.md) | Separate control-plane DbContext and access module | Accepted |
| [0019](adr/0019-entitlement-as-an-explicit-record.md) | Entitlement as an explicit record, reconciled from the subscription | Accepted |
| [0020](adr/0020-store-ingestion-authentication.md) | Store ingestion: tokens, replay protection, signed payloads | Accepted |
| [0021](adr/0021-domain-verification-before-connected.md) | A store is Connected only once it has proven its domain | Accepted |
| [0022](adr/0022-realtime-subscriptions-are-server-assigned.md) | Realtime subscriptions are server-assigned, never client-chosen | Accepted |
| [0023](adr/0023-a-ported-store-is-single-tenant.md) | A ported store is single-tenant | Accepted |
| [0024](adr/0024-base-store-versus-optional-feature.md) | What belongs in the base store versus an optional Feature | Accepted, revised 2026-08-25 |
| [0025](adr/0025-provisioning-is-a-job-with-manual-steps.md) | Provisioning is a job with manual steps | Accepted |
| [0026](adr/0026-knight-records-backups-it-does-not-take-them.md) | KNIGHT records store backups; it does not take them | Accepted |
| [0027](adr/0027-the-restore-drill-is-the-backup-test.md) | The restore drill is the backup test, and it runs in CI | Accepted |
| [0028](adr/0028-staged-rollouts-with-a-single-store-canary.md) | Staged rollouts with a single-store canary | Accepted |
| [0029](adr/0029-one-slug-for-the-catalogue-and-the-package.md) | One slug for the commercial catalogue and the deployable package | Accepted |
| [0030](adr/0030-what-store-data-may-reach-a-model-provider.md) | What store data may reach a model provider | Accepted |
| [0031](adr/0031-database-extensions-are-declared-not-migrated.md) | Database extensions are declared, not migrated | Accepted, amends 0016 |

**Revision note:** the first documentation revision treated a Feature as an
entitlement flag. ADR 0014 corrects that. Where any older, un-updated document
still implies "feature = flag", this correction wins.

## Rules that must never be broken

1. KNIGHT must never become the business backend of a customer store.
2. Customer stores stay independently deployable.
3. KNIGHT must never depend on a store's database schema, and never connects to a store database.
4. Customer isolation is enforced server-side, always.
5. The frontend is never the source of truth for authorization.
6. Feature entitlement is enforced by backend systems (KNIGHT and the store).
7. No secrets in source control, logs, or API responses.
8. No microservice, broker, or orchestrator without a written justification —
   and a Feature Package is **not** a microservice.
9. A Feature is implemented once and delivered automatically; feature code is
   never duplicated or hand-installed per customer.
10. Entitlement (paid for) and installation (deployed and healthy) are separate
    facts and are never collapsed into one boolean.
11. KNIGHT delivers **signed, verified** artifacts through a fixed, typed job
    vocabulary — never arbitrary code or commands.
