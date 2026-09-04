# Post-Phase-30 hardening backlog

The self-service SaaS transition (phases A–G) is built. What remains splits into
two: **product-owner decisions** that unblock the real (rather than simulated)
automation, and **production-hardening work** — most of it raised by an external
architecture review and recorded here honestly, including where the review
described a risk the code already anticipates.

The review's own scores, for reference: Architecture 7/10, Code quality 9/10,
Security 4/10, Performance 8/10, Testing 10/10, DevOps 5/10. The security score
is harsher than the defensive depth warrants (server-side entitlement
enforcement, a fail-closed customer-isolation filter, SSRF blocked at the socket,
replay guards, optional mTLS, redaction on ingest, a closed agent job vocabulary,
signed and digest-verified artifacts) — but it points at one genuine structural
risk, signing-key custody, which is real and tracked as R16/R21.

## Product-owner decisions (unblock real automation)

- [ ] **Payment provider.** The seam is `IPlatformPaymentProvider`. A **Stripe
  adapter** now ships behind it (`StripePaymentProvider`, real Stripe webhook
  signature verification, off unless `PlatformBilling:Stripe:SecretKey` is set);
  the simulated provider stays the default. Choosing Stripe (or adding another
  adapter) and configuring live keys is the decision left.
- [ ] **Hosting platform.** The seam is `IInfrastructureAdapter`. A real adapter
  (a cloud provider, or Kubernetes/Nomad) replaces `SimulatedInfrastructureAdapter`
  for `server`/`instance`/`domain-tls`. Until then the simulated adapter runs the
  whole journey locally.
- [!] **External security review of the code-delivery path** (R16). The one item
  nobody inside the project can close; the briefing pack is in
  [`security/external-review-scope.md`](security/external-review-scope.md).

## Hardening raised by the review

Priority uses the review's P0–P3.

- [ ] **P0 — Signing-key custody in production.** The private artifact-signing key
  is configuration-backed today (`FeatureArtifacts:Keys`). The abstraction is
  already `IFeatureArtifactSigner`, written explicitly as "the shape a KMS-backed
  implementation will take" — so the work is a **KMS/HSM-backed signer**
  (AWS KMS, Azure Key Vault or Vault Transit) selected by config, so the private
  key never sits in a settings file or a CI secret. Verification stays as-is
  (public keys only). *Nuance the review overstated: a leaked key lets an attacker
  publish a malicious **signed** package — it is not arbitrary command execution.
  The agent has a closed job vocabulary and runs no shell (`feature-delivery.md`
  §15); staged single-store canary rollout (`adr/0028`) is the blast-radius
  control until the review lands.*
- [ ] **P0/P1 — Delivery model at scale (immutable vs. runtime install).** The
  review recommends per-tenant immutable container images over installing signed
  packages onto stateful servers. A serious **ADR-level comparison** is worth
  doing rather than a foregone switch: a base store image is already signed and
  versioned, a Feature can already be an `external_service` (no code injection at
  all) or a non-Django runtime, delivery is verify-digest-then-install with
  rollback, and **drift is already detectable** (the store reports installed
  state, KNIGHT holds intended state, a `feature.drift` alert fires on the gap).
  The immutable model trades that for build-per-purchase and image sprawl.
- [ ] **P1 — Replace the Bash installers with IaC.** `install.sh`/`knightctl.sh`
  are a single-server stopgap (phase 11). Production provisioning should be
  Terraform/Ansible, plugged in behind the same `IInfrastructureAdapter` the
  self-service automation already uses.
- [ ] **P1 — Database connection pooling.** Add PgBouncer in front of PostgreSQL
  before the store count makes independent connections the bottleneck. The load
  test (1,882 req/s) shows headroom now; this is a scaling item, not a fire.
- [ ] **P2 — Automate shared-secret issuance/rotation end to end** (phase 24 is
  partial): store credentials and agent tokens issued and rotated with no manual
  step.
- [ ] **P2 — Agent least privilege.** Run the agent under a dedicated user with
  the narrowest filesystem and process rights, confined by systemd sandboxing /
  AppArmor/SELinux.
- [ ] **P2 — Formal outbox for billing → provisioning.** The webhook→provisioning
  wire is idempotent and commits the activation before provisioning runs (a
  failed provisioning never un-takes a payment; the coordinator retries), but a
  transactional **outbox** would make the at-least-once guarantee explicit and
  survive a process death between commit and enqueue.
- [ ] **P3 — Tenant data export / offboarding tooling.** The deprovision pipeline
  already models an `Export` step and a retention window before purge
  (`adr/0026`); this is turning that manual step into a standard, self-serve
  export.
- [ ] **P3 — Push-based telemetry.** Move from polling to an OpenTelemetry
  collector per store. The meter and traces already exist (phase 7); this is the
  transport.

## What the review said to keep (and we agree)

The .NET modular monolith control plane, the per-store database and application
isolation, the discipline of architecture tests and ADRs, and not reaching for
microservices early. None of that changes.
