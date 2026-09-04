# Phase 30 G — operations, verified

The operator side of self-service (docs/self-service-saas-plan.md §12, G):
"provisioning retry/resume, entitlement grant/revoke, suspend/restore, agent
monitoring, audit — extending the existing admin screens." Most of that already
existed; the one screen the operations dashboard was missing was a **provisioning
operations view**, and that is what this phase adds.

## What was already there

- **Entitlement grant/revoke** — the customer detail's entitlements tab, over
  `POST /customers/{id}/entitlements` and `.../{featureId}/revoke`.
- **Suspend / restore** — the customer detail's lifecycle (activate / suspend /
  archive), which cascades to the customer's stores, plus subscription
  suspend/activate endpoints.
- **Agent monitoring** — the infrastructure screen (servers, agents, metrics).
- **Audit** — the audit-log screen.

## What was added

A **Provisioning** screen (`features/provisioning/ProvisioningPage`, nav under
Operations, gated on `store.view`): every provisioning and deprovisioning run —
self-service and operator-started alike — filterable by state (All / Running /
Awaiting a person / Failed), each row showing the store, the kind, the state and
a progress bar. Opening a run shows its full step timeline with each step's
status and detail, and — for a run still in flight and to an operator with
`store.provision` — **Retry** (a failed run), **Resume** (re-evaluate a run
waiting on a step) and **Cancel**. It reads the existing
`/api/v1/provisioning` endpoints; no new backend was needed.

The customer's own portal (phase F) shows the friendly version of the same runs;
this is the operator's, with the raw steps and the levers.

## Verified in a browser

Signed in as a platform SuperAdmin against the live API and opened
`/provisioning`:

- the list showed the self-service run created in the phase-F walk (**Succeeded,
  9/9**) alongside older runs;
- the **Awaiting a person** filter narrowed the list to the one waiting run (a
  `?state=AwaitingOperator` query);
- opening the succeeded run showed all nine steps **Done** with their details
  ("Registered as '…' in Production, hosting SharedManaged", "The store passed its
  health check and is now Active", …);
- opening the waiting run showed **Resume** and **Cancel** in the drawer footer; a
  finished run shows no actions.

**Verified on 2026-09-04.** Steps to repeat are the phase-F steps plus: bootstrap
a platform admin (`dotnet run --project backend/tools/Knight.Bootstrap --
--control-plane --email you@example.test`), sign in, and open **Provisioning**
from the Operations section of the sidebar.

## Phase 30 status

A through G are built. What remains is not implementation but the two
product-owner decisions §11 always named — the **payment provider** and the
**hosting platform** — which the simulated adapters stand in for so the whole
journey, its acceptance test and both portals run locally today.
