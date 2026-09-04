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
- [x] **P2 — Automate shared-secret issuance/rotation end to end.** **Built**, the
  cross-repo way it had to be — rotate-on-handshake. When a store authenticates with
  a credential nearing expiry, the handshake rotates it in place and hands back the
  replacement in the response (`rotatedCredential`): the one authenticated moment
  KNIGHT can deliver a plaintext secret it otherwise only ever stores hashed. The
  old credential keeps working through its grace window — the store's margin to
  switch over — and only a still-active credential is rotated, so a store that keeps
  presenting the old secret does not trigger a fresh rotation every handshake. The
  KNIGHT side is off unless a deployment opts in with `Stores:CredentialLifetime`
  and `Stores:CredentialRotationThreshold`; with neither set, rotation stays
  operator-initiated exactly as before. The store side (the .NET agent, and the copy
  vendored into BojanStore) adopts a delivered replacement, persists it, and
  authenticates with it from the next handshake on — without which a rotation would
  simply lock the store out when grace ended, which is why the KNIGHT-only sweep was
  never built. The shared contract schema carries the new field so both sides agree
  on its shape. Covered by an end-to-end handshake test on the KNIGHT side and an
  adoption test on the store side.
  *Not yet done: the Python/node reference stores' `knight_integration` adopters,
  for contract parity — the real store (BojanStore, .NET) is complete.*
- [x] **P2 — Agent least privilege.** `agent/deploy`: a dedicated system user, a
  fully-sandboxed systemd unit (no capabilities, `@system-service` syscall filter,
  restricted address families, read-only system with writes only to the state and
  store roots) and an AppArmor profile as a second confinement. Authored; validate
  on a live host before enforcing (no Linux host in CI).
- [x] **P2 — Formal outbox for billing → provisioning.** Built: the webhook writes
  an `ActivationOutboxEntry` in the same unit of work as the activation, and a
  platform-scoped `OutboxDispatcherWorker` drains it — so a crash between the
  activation commit and the store's creation no longer leaves a paid subscription
  with no store. At-least-once (the provisioning listener is idempotent), with
  backoff and a dead-letter ceiling. Migration `ActivationOutbox`; unit tests + the
  acceptance test.
- [x] **P3 — Tenant data export / offboarding tooling.** Built: `GET /api/v1/me/export`
  hands a customer KNIGHT's whole record of them — store metadata, subscription,
  entitlements, provisioning history and telemetry counts — as a self-serve JSON
  download (a "Download my data" button in the portal). `ITenantExportReader` reads
  only what KNIGHT holds, never a store's own business data. The deprovision
  pipeline's `Export` step now produces this automatically (via `IStoreExporter`,
  writing a durable snapshot before purge) — no longer a manual step. Covered by
  the acceptance test and a store-export test.
- [!] **P3 — Push-based telemetry.** The meter and traces already exist (phase 7,
  off by default); moving from KNIGHT polling stores to stores pushing to an
  OpenTelemetry collector is a **cross-repo, infrastructure** change — the store
  side must emit OTLP and a collector must run — not a self-contained control-plane
  task. Sequenced with the hosting-platform decision, like the real infrastructure
  adapter.

## What the review said to keep (and we agree)

The .NET modular monolith control plane, the per-store database and application
isolation, the discipline of architecture tests and ADRs, and not reaching for
microservices early. None of that changes.
