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

- [~] **P0 — Signing-key custody in production.** **Built:** the KNIGHT-side
  signer now has a KMS path. `FeatureArtifacts:Signer=kms` selects
  `KmsArtifactSigner`, which delegates signing to an external key store through the
  new `IKmsSigner` seam (an `HttpKmsSigner` ships for an internal KMS proxy or
  Vault Transit; an AWS KMS / Azure Key Vault SDK adapter is a drop-in
  `IKmsSigner`) — so the private key never enters the process. Verification is
  local and byte-for-byte the config signer's, both now sharing
  `ArtifactSignatureCodec`. Default stays `config` for development and CI. 6 unit
  tests. **Remaining:** stand up a real KMS and set `FeatureArtifacts:Kms`, and
  move the **offline packaging tool** (`features/tools/knight_package.py`) to sign
  through the same KMS so the key leaves that environment too.
  *Nuance the review overstated: a leaked key lets an attacker publish a malicious
  **signed** package — it is not arbitrary command execution. The agent has a
  closed job vocabulary and runs no shell (`feature-delivery.md` §15); staged
  single-store canary rollout (`adr/0028`) is the blast-radius control until the
  review lands.*
- [x] **P0/P1 — Delivery model at scale (immutable vs. runtime install).**
  Decided in [`adr/0036`](adr/0036-feature-delivery-runtime-install-versus-immutable-images.md):
  keep verify-then-install as the model; adopt immutable per-tenant images as a
  hosting **option** behind `IInfrastructureAdapter` (where rollback becomes a
  container swap), chosen per store rather than for all; prefer `external_service`
  for Features that can be one. Not a rewrite — the desired-state contract is
  unchanged, and drift/supply-chain are already addressed by the reconciliation
  loop, signing (now KMS-capable), digest verification, the closed agent
  vocabulary and the canary.
- [~] **P1 — Replace the Bash installers with IaC.** **Built:**
  `infrastructure/iac` — an Ansible role that is the provider-agnostic,
  idempotent replacement for `install.sh`'s logic (packages, .NET toolchain,
  PostgreSQL role/db, Redis, checkout/publish/migrate, a hardened systemd unit,
  the single-hostname nginx site with certbot TLS, the first admin, the nightly
  backup), plus a Terraform reference for the machine. YAML syntax-checked.
  **Remaining:** run it against a live host end to end before ticking it done (no
  Ansible/Terraform in CI, and the hosting platform is unchosen) — the same bar
  `install.sh` cleared in phase 11. `install.sh` stays the verified path until
  then.
- [x] **P1 — Database connection pooling.** PgBouncer runs in front of PostgreSQL
  (compose, transaction mode, port 6432). No application change — EF Core/Npgsql
  use no server-side prepared statements by default. Verified by running the API
  through it. See `infrastructure/database/README.md`.
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
