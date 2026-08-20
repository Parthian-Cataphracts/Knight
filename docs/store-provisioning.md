# Store Provisioning

Status: **authoritative proposal**. Depends on
[`feature-delivery.md`](feature-delivery.md).

Provisioning is the path from "a customer signed up" to "a working store with
its base Features installed and healthy". It reuses the Feature delivery
pipeline rather than inventing a parallel one.

## 1. Flow

```
Customer created
      │
Plan selected  ──► Subscription + entitlements created
      │
Infrastructure decided (SharedManaged | DedicatedManaged | CustomerManaged)
      │
Base store instance created           ProvisioningJob step 1
      │  (base Django image + DB + Redis + agent)
      │
Store registered in KNIGHT            step 2 — credentials issued
      │
Agent registered, handshake           step 3
      │
Base Feature plan resolved            step 4 — from Plan → entitlements
      │
Installation jobs executed            step 5 — ordinary feature-delivery jobs
      │
Configuration applied                 step 6
      │
Domain + TLS wired                    step 7
      │
Health check                          step 8
      │
   Store Ready
```

Each step is recorded on a `ProvisioningJob` with the same idempotency,
progress reporting, audit, and failure semantics as an installation job.

## 1a. The implemented pipeline

The steps as they exist in code, in order, with who carries each one out
([`adr/0025`](adr/0025-provisioning-is-a-job-with-manual-steps.md)):

| Step | Mode | Finished when |
|---|---|---|
| `server` | manual | a machine is recorded against the store |
| `instance` | manual | a store instance built from the base image answers |
| `store-record` | automatic | the store is registered with KNIGHT |
| `credentials` | automatic | the store holds a usable credential |
| `agent` | automatic | an agent on the store's server has enrolled |
| `base-features` | automatic | every entitled Feature is installed |
| `configuration` | automatic | the store has completed a handshake |
| `domain-tls` | manual | ownership of the primary domain is proven |
| `healthcheck` | automatic | the link is `Connected` — and only then is the store activated |

Deprovisioning runs `disable-features` → `revoke-access` → `stop-ingestion` →
`retain` → `export` (manual) → `purge`.

An automatic step is never completed by hand. A manual step holds the run in
`AwaitingOperator` and is completed by a named operator, audited. A coordinator
re-evaluates unfinished runs on a timer, because everything an automatic step
waits for happens in another module and notifies nobody.

Retention is resolved once, when a deprovisioning run starts: the customer's
negotiated override wins, then the plan's promise, then the deployment default.

## 2. What is automated now vs later

| Step | Initial release | Later |
|---|---|---|
| Store record + credentials | automated | — |
| Agent registration | automated (one-time provisioning token) | — |
| Base Feature installation | automated | — |
| Configuration + domain metadata | automated | — |
| Creating the server/VM itself | **manual**, recorded as metadata | possibly automated per provider |
| Creating the Django instance + DB | **manual or scripted** | automated from a base image |
| TLS/DNS | manual | automated |

KNIGHT is honest about the boundary: the parts it does not automate are
represented as manual steps on the job, not pretended away. This keeps the
initial scope achievable while the state machine is already correct.

## 3. Base store image

A base Django store image contains: the store skeleton, the
`knight_integration` layer, no business features beyond the plan's base set,
and a pinned store version (`storeVersion`) that Feature compatibility ranges
are checked against.

The base image is versioned and published like a Feature artifact — signed,
digest-verified, and recorded in the registry as a `StoreImage`. It carries the
`storeVersion` an instance built from it reports, so Feature compatibility
ranges resolve against a recorded fact rather than a deployment note. Images are
published (`POST /api/v1/store-images`) from an already-signed package uploaded
to `POST /api/v1/artifacts`: the digest KNIGHT computes from the stored bytes is
what the signature is checked against, and no signing key is ever present in the
web application.

An operator names the image when ticking off the `instance` step; a version that
is not published is refused, so "which image is this store on" stays answerable
months later.

## 3a. Dedicated infrastructure

A server registered as `DedicatedManaged` records the customer it belongs to,
and a store may only be placed on a dedicated machine belonging to its own
customer — that check is what makes "dedicated" a fact rather than a billing
label. A placement onto a decommissioned machine, or onto one registered for a
different environment, is refused for the same reason.

A store on dedicated or customer-managed infrastructure may additionally be
bound to a client certificate. Mutual TLS is optional and never offered on
shared hosting, where the certificate would sit beside other customers' stores.
Once bound, the thumbprint is checked on the handshake **and** on every
authenticated ingest call, because a token minted at handshake lives for half an
hour and a stolen one does not carry the certificate with it.

## 4. Failure handling

A failed provisioning job leaves the store in `Provisioning` with a recorded
failed step and reason. It is retryable from the failed step. A store never
becomes `Active` without a passing health check, and a half-provisioned store
is never billed as operational.

## 4a. Backups

KNIGHT records backup reports and never takes or holds a backup
([`adr/0026`](adr/0026-knight-records-backups-it-does-not-take-them.md)). A
store reports to `POST /api/v1/ingest/backups`; a failure raises `backup.failed`
on the spot, and a store nobody has reported a successful backup for raises
`backup.overdue` from the observability sweep.

## 5. Deprovisioning

Archiving a store: disable Features → revoke credentials and agent token →
stop ingestion → retain data for the contractual window → purge on expiry, with
an exportable backup produced before purge. Every step audited.
