# 0036 — Feature delivery: verify-then-install, with immutable images as a hosting option, not a rewrite

**Status:** accepted

Relates to [`0014`](0014-features-as-deployable-packages.md) (a Feature is a
deployable package), [`0015`](0015-feature-delivery-mechanism.md) (how it is
delivered), [`0016`](0016-feature-migration-and-removal-policy.md) (migration and
rollback), [`0028`](0028-staged-rollouts-with-a-single-store-canary.md) (canary),
[`0032`](0032-a-feature-declares-its-runtime.md) and
[`0033`](0033-api-driven-features.md) (runtimes and external-service Features).
Raised by an external architecture review; recorded in
[`../hardening-backlog.md`](../hardening-backlog.md).

## Context

An external review's strongest structural objection was to the delivery model.
Today KNIGHT delivers a Feature by having the store's agent fetch a signed
artifact, verify it, install it, run its migrations and reload the app. The
review argued this is, at a thousand stores, a *configuration-drift* and
*supply-chain* liability, and recommended replacing it with **immutable
per-tenant container images**: on purchase, a CI pipeline builds a Docker image
containing the base store plus exactly the Features that customer is entitled to,
and delivery becomes "pull the new image and swap the container."

The objection deserves a decision on the record rather than a reflex either way,
because both models are legitimate and the review is right about the failure it
names — it is just not the only fact in play.

### What is already true, and the review under-counted

- **The base store is already an immutable, signed, versioned image.** The
  argument is only about the Features layered on top, not the whole store.
- **Drift is already detectable.** The store reports what is installed; KNIGHT
  holds what it intended; a `feature.drift` alert fires on the difference, and the
  provisioning/reconciliation path acts on it. Drift is a monitored condition
  here, not a silent one.
- **A Feature need not inject code at all.** Since [`0033`](0033-api-driven-features.md)
  a Feature can be an `external_service` — a network service the store calls — and
  since [`0032`](0032-a-feature-declares-its-runtime.md) it can target a non-Django
  runtime. Neither touches the store's filesystem.
- **The supply-chain path is defended in depth, not open.** Artifacts are signed
  and digest-verified before anything runs; the agent has a *closed job
  vocabulary* and runs no shell, so the channel is not arbitrary code execution;
  a single-store **canary** ([`0028`](0028-staged-rollouts-with-a-single-store-canary.md))
  bounds the blast radius of a bad release; and signing-key custody is moving to a
  KMS (hardening backlog P0). "RCE by design" overstates it: the realistic risk is
  a *leaked signing key producing a malicious signed package*, which is exactly
  what the external security review (R16) targets.

### What the review is right about

- **`pip install` + migrate on a long-lived server is stateful**, and dependency
  resolution on the target can diverge between stores over time.
- **A schema-reversing rollback in production can lock tables.** Blue/green with
  two images makes rollback a traffic switch instead.
- **A read-only store filesystem is a stronger security posture** than one the
  agent writes into.

## Decision

**Keep verify-then-install as the delivery model. Do not rewrite it into
per-tenant immutable images. Adopt immutable-image delivery as an option that the
hosting-platform adapter enables, chosen per store rather than for all.**

Concretely:

1. **The desired-state contract does not change.** Delivery is, and stays, "the
   store's effective entitlements are the truth; make the store match them." That
   is what `IBaseFeatureInstaller` and the delivery engine already do. Whether the
   agent achieves it by installing a package or by pulling an image is an
   implementation of the *same* contract.

2. **Immutable images become a delivery strategy behind `IInfrastructureAdapter`.**
   When the hosting platform is one that runs containers (Kubernetes/Nomad, or a
   provider adapter that builds per-tenant images), an entitlement change triggers
   a **per-tenant image build** and a **container swap**, and rollback is a swap
   back — no on-server `pip install`, no schema-reverse under load. The agent
   takes an "pull this image" job as readily as an "install this package" one; the
   job channel's vocabulary already separates *what* from *how*.

3. **Runtime install remains for shared-hosting stores**, where a per-tenant image
   per purchase does not pay for itself: build time on every checkout, image
   sprawl, a registry per tenant, and a cold-start on every Feature toggle. A shop
   on Basic toggling one add-on should not trigger a container build and a
   registry push.

4. **Prefer `external_service` for Features that can be one.** A Feature delivered
   as a service is injected into nothing and drifts nowhere; it is the cleanest
   answer to the review's concern and already exists. New Features are weighed for
   it first.

## Consequences

- No rewrite, and no destabilising a delivery engine that is complete, tested and
  exercised by the delivery drill in CI. The review scored testing 10/10; that is
  the thing a wholesale rewrite would spend.
- The immutable-image path is unblocked by, and sequenced behind, the
  **hosting-platform decision** the self-service plan already names (§11): the
  same adapter that makes `server`/`instance`/`domain-tls` real is where per-tenant
  image build-and-swap lands. It is a hosting capability, not a redesign.
- Drift and supply-chain risk keep being addressed where they actually are: drift
  by the reconciliation loop and its alert, supply chain by signing (now
  KMS-capable), digest verification, the closed agent vocabulary, the canary, and
  the pending external review — none of which the image model would remove the
  need for (an image is signed and pulled for the same reasons a package is).
- A follow-up is owed: when the container hosting adapter is built, a short design
  note on the per-tenant image build pipeline (cache strategy, registry-per-tenant
  vs shared-with-labels, and how migrations run against the new image before the
  swap) — not in this ADR, which decides the *shape*, not the build system.

## What this does not decide

It does not choose the hosting platform (still §11, product owner). It does not
commit to Kubernetes over Nomad or a managed builder. And it does not close the
external security review (R16), which stands regardless of delivery model.
