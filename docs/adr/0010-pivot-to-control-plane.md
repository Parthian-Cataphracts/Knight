# 0010 — Pivot from shared multi-tenant SaaS to control plane

- Status: **Accepted**
- Date: 2026-08-18
- Supersedes (in part): 0001, 0002, 0003, 0004

## Context

The repository (commit `39fe127`) implements a shared multi-tenant food-service
SaaS: one ASP.NET Core application owning catalog, ordering, checkout, payment,
promotions, fulfillment, and delivery for every tenant, in one PostgreSQL
database with row-level tenant scoping.

The KNIGHT specification describes a different system: a **control plane** that
manages independent customer stores, each an autonomous Django application with
its own domain, database, and deployment. The specification explicitly forbids
KNIGHT owning store business logic, depending on store schemas, or putting all
independent stores in one database.

These cannot both be true of one product.

## Options considered

1. **Two planes in one repo** — keep the food SaaS as a "shared tier store
   engine" and add the control plane beside it. Cheapest short term, but leaves
   two competing definitions of "store" and permanently violates Rule 1 for the
   shared tier.
2. **Full pivot** — build the control plane in .NET, move the business domain to
   a Django store template, retire the store modules from .NET.
3. **Separate project** — leave this repo untouched and start KNIGHT elsewhere.
   Loses the reusable identity/authorization/audit/test infrastructure and
   fragments the work.

## Decision

**Option 2 — full pivot**, executed as a strangle, not a rewrite: freeze the
store business modules, build the control-plane core alongside them, build the
Django reference store, port the domains, then remove the store modules from
the .NET solution. Sequencing and exit criteria are in
[`../migration-plan.md`](../migration-plan.md).

Retained from the existing codebase: the API host and request pipeline,
`Identity`, `FeatureManagement`, the `Tenancy` lifecycle patterns (reshaped into
`Customer` + `Store`), audit recorders, EF Core conventions, and all three test
projects including the architecture tests.

## Consequences

**Positive** — the product matches its specification; store business logic lives
where the specification puts it; the reusable half of the existing work is kept;
the pivot is reversible stage by stage.

**Negative** — a large port of seven modules to Django; ADRs 0001–0009 become
partly historical; the shared-database model and its migrations are eventually
discarded; delivery of new business features pauses during the pivot.

**Risks** — see `../risks.md` R1 (possible real data), R2 (port effort).
