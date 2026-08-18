# Migration Plan — From Shared SaaS to Control Plane

Status: **authoritative**. Decision recorded in
[`adr/0010`](adr/0010-pivot-to-control-plane.md).

## 1. The problem

The repository implements a shared multi-tenant food-service SaaS. The
specification requires a control plane over independent Django stores. Seven of
eleven .NET modules are store business logic that the specification forbids
KNIGHT from owning, and all tenant data sits in one shared database.

## 2. Strategy

**Strangle, do not big-bang.** The .NET solution stays; the control-plane
domain is built inside it while the store business modules are frozen, then
extracted to the Django store template, then removed.

```
Stage A   Freeze store business modules (no new features, tests keep running)
Stage B   Build the control-plane core alongside them (new DbContext, new modules)
Stage C   Build the Django reference store + integration layer
Stage D   Port the frozen business domains into the Django store template
Stage E   Remove the store modules, endpoints, contracts, and migrations from .NET
Stage F   Delete the legacy shared database schema
```

Nothing in Stage E happens until the Django store covers the same behaviour and
its own tests pass.

## 3. Disposition of every existing module

| Module | Action | Notes |
|---|---|---|
| `Identity` | **Keep** | Add `customerId` scoping, MFA, store/agent principal types |
| `Tenancy` | **Transform** | `Tenant` → `Customer` + `Store`; reuse the lifecycle state machine and domain normalisation |
| `FeatureManagement` | **Keep + extend** | Becomes *entitlements* driven by `Subscription`. The registry, packaging, installation, and job machinery of [`feature-delivery.md`](feature-delivery.md) is new code, not a refactor of this module |
| `Catalog` | **Port to Django** | Products, categories, variants, modifiers, media |
| `Customer` | **Port to Django** | End-consumer of a store (rename to avoid clashing with KNIGHT's `Customer`) |
| `Ordering` | **Port to Django** | |
| `Checkout` | **Port to Django** | Preserve the idempotency design (ADR 0008) |
| `Payment` | **Port to Django** | Preserve obligation vs attempt model (ADR 0009) |
| `Promotions` | **Port to Django** | |
| `Fulfillment` | **Port to Django** | Preserve the historical snapshot model (ADR 0007) |
| `Delivery` | **Port to Django** | Server-side delivery pricing must stay server-side |

### Terminology collision

`Customer` means two different things. In KNIGHT it is the paying business. In
a store it is the end consumer. The Django app is named `shoppers` (or
`store_customers`) to keep the distinction unambiguous.

### Note on the ported business domains

The seven ported domains become part of the **base store**, not Features. A
capability only becomes a Feature when it is optional, separately priced, or
separately versioned. Deciding which ported pieces eventually become optional
Features is a Phase 8 exercise, and the split must be justified per capability
— not applied wholesale.

## 4. Infrastructure to reuse as-is

Request pipeline and correlation ids, ProblemDetails handling, rate-limiting
policies, health endpoints, OpenAPI/Scalar, audit recorder pattern, EF Core
conventions and repository style, the three test projects, architecture tests,
and the Docker Compose setup.

## 5. Data migration

None of the existing tenant data is production data (single initial commit, no
live customers identified). The plan therefore assumes a **clean control-plane
schema** rather than a data migration. If real tenants exist, this assumption
must be revisited before Stage E — see `risks.md` R1.

## 6. Frontend

There is nothing to migrate — the frontend is empty. The planned Next.js
storefront/tenant-admin layout is discarded; `frontend/knight-dashboard/`
(React + Vite + TS) is created fresh, and `frontend/README.md` is rewritten.

## 7. Documentation migration

- New authoritative docs live directly under `docs/` (this set).
- Previous-product docs stay under `docs/architecture/`, `docs/api/`,
  `docs/database/`, `docs/security/`, each marked as legacy.
- ADRs 0001–0009 are retained; 0010 records what they supersede. The duplicate
  `0006`/`0007` numbering is left as-is historically and never reused.
- Stray docs under `backend/docs/` move to `docs/` during Stage B.

## 8. Exit criteria per stage

| Stage | Done when |
|---|---|
| A | Freeze noted in TODO and README; no new work merged into store modules |
| B | Control-plane modules, DbContext, migrations, and auth work with green tests |
| C | Reference Django store registers, reports health, ships errors, syncs entitlements |
| D | Ported business domain passes its own test suite in Django |
| E | .NET solution contains no store business module; architecture tests enforce it |
| F | Legacy tables dropped; only control-plane schema remains |
