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
digest-verified, and recorded in the registry.

## 4. Failure handling

A failed provisioning job leaves the store in `Provisioning` with a recorded
failed step and reason. It is retryable from the failed step. A store never
becomes `Active` without a passing health check, and a half-provisioned store
is never billed as operational.

## 5. Deprovisioning

Archiving a store: disable Features → revoke credentials and agent token →
stop ingestion → retain data for the contractual window → purge on expiry, with
an exportable backup produced before purge. Every step audited.
