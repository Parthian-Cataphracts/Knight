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
| **Features** | [`features/`](../features/) — installable Django packages; promotions and delivery zones ship this way ([`adr/0024`](adr/0024-base-store-versus-optional-feature.md)) |
| **Dashboard** | `frontend/knight-dashboard/` — every screen against the real API |
| **Not built yet** | Provisioning (phase 9), outbound email, and the hardening in phase 10 |

Detailed inventory: [`current-state-analysis.md`](current-state-analysis.md).

## Authoritative documents (target architecture)

Read in this order:

1. [`current-state-analysis.md`](current-state-analysis.md) — what is really in the repo today, file by file
2. [`architecture.md`](architecture.md) — target system architecture, containers, modules, boundaries
3. [`feature-delivery.md`](feature-delivery.md) — **the Feature registry, packaging, and installation pipeline** (read before anything about features)
4. [`domain-model.md`](domain-model.md) — control-plane entities and relationships
5. [`api-contracts.md`](api-contracts.md) — Dashboard↔KNIGHT, KNIGHT↔Store, Agent↔KNIGHT contracts
6. [`store-integration.md`](store-integration.md) — the Django integration layer and its lifecycle
   (implemented in [`stores/reference-store/`](../stores/reference-store/README.md);
   the wire contract both sides test against is
   [`contracts/store-integration.schema.json`](contracts/store-integration.schema.json))
7. [`store-provisioning.md`](store-provisioning.md) — from customer signup to a ready store
8. [`authentication.md`](authentication.md) — human auth, store auth, agent auth
9. [`authorization.md`](authorization.md) — roles, permissions, customer isolation
10. [`frontend-architecture.md`](frontend-architecture.md) — React + Vite + TS dashboard, RTL, responsive
11. [`observability.md`](observability.md) — logs, metrics, traces, correlation, errors, delivery visibility
12. [`deployment.md`](deployment.md) — environments, feature delivery pipeline, config, secrets
13. [`security-threat-model.md`](security-threat-model.md) — threats and required controls
14. [`migration-plan.md`](migration-plan.md) — how to get from the current repo to the target
15. [`risks.md`](risks.md) — open risks, contradictions, unresolved decisions
16. [`development.md`](development.md) — how to run, test, and contribute
17. [`phase-3-verification.md`](phase-3-verification.md) — the exact steps to bring KNIGHT, a store and the dashboard up together and see the link work

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
