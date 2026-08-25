# Risks, Contradictions, and Open Decisions

Status: **living document**. Update whenever a risk is resolved or discovered.

## 1. Resolved contradictions

| # | Contradiction | Resolution |
|---|---|---|
| C1 | Repo implements a shared multi-tenant SaaS; spec forbids KNIGHT owning store business logic | Full pivot to the control plane — [`adr/0010`](adr/0010-pivot-to-control-plane.md) |
| C2 | Repo docs specify Next.js tenant frontends; the requirement is a React + TS admin dashboard | React + Vite + TS single dashboard — [`adr/0011`](adr/0011-react-vite-dashboard.md); storefronts leave the repo |
| C3 | `Customer` means the paying business in KNIGHT and the end consumer in a store | KNIGHT keeps `Customer`; store app is named `shoppers` (`migration-plan.md` §3) |
| C4 | Spec says both ".NET is the primary backend" and "Python/Django may be used for AI capabilities" | AI capabilities are a future separate service; core control plane stays .NET (`architecture.md` §10) |
| C5 | Previous revision modelled a Feature as an entitlement flag, implying manual per-customer implementation | Features are versioned deployable Django packages delivered automatically — [`adr/0014`](adr/0014-features-as-deployable-packages.md); entitlement and installation are now separate concepts |

## 2. Open risks

| # | Risk | Impact | Mitigation / owner decision needed |
|---|---|---|---|
| R1 | ~~Existing tenant data may be real production data~~ **Resolved 2026-08-20** | A clean-schema pivot would destroy it | The product owner confirms the frozen store modules and the legacy shared schema hold only development and test data. Phase 8 may drop them without an export path, which removes the largest unknown from that phase. The confirmation is recorded here rather than assumed: dropping a schema is not reversible, and the next person to read this needs to know somebody actually checked |
| R2 | Porting 7 modules to Django is a large, easily underestimated effort | Pivot stalls half-finished, leaving two incoherent halves | Stage-gated plan; store modules frozen, not deleted, until Django parity |
| R3 | KNIGHT must reach store management APIs across customer networks | Firewalls/NAT make polling impossible for some customers | Support both pull (poll) and push (store heartbeat) from day one |
| R4 | Ingestion volume (errors, logs, metrics) can outgrow PostgreSQL | Slow dashboards, storage blowout | Partitioning + retention from the first migration; measure before adding a broker or a TSDB |
| R5 | A store can flood KNIGHT and degrade it for everyone | Shared-fate outage | Per-store rate limits, batch caps, bounded queues, backpressure |
| R6 | Log ingestion may carry personal data from store customers | Legal exposure | Classify data, scrub at the source, document retention, make log shipping opt-in |
| R7 | Domain ownership is unverified at registration | Telemetry hijack | DNS/well-known verification before `Connected` (`security-threat-model.md` §4) |
| R8 | Credential rotation without a grace window breaks live stores | Self-inflicted outage | Dual-active credentials with a grace period |
| R9 | Feature entitlement caching in stores can fail open | Unpaid feature access, or a storefront outage | Signed payload, TTL, last-known-good with a bounded grace window |
| R10 | The agent is a privileged component on customer servers | Compromise reaches the customer's host | Push-only, no command channel, least privilege, signed releases |
| R11 | Two ADRs numbered 0006 and two numbered 0007 | Confusing references | Numbers frozen; never reused; new ADRs start at 0010 |
| R12 | Duplicate/stray docs under `backend/docs/` | Future agents read the wrong file | Consolidate into `docs/` during Stage B |
| R13 | Building the dashboard before contracts stabilise | Rework | Generate types from OpenAPI; build screens after their endpoints exist |
| R14 | ~~Billing scope is undefined (invoicing only vs payment processing)~~ **Resolved in phase 2** | Scope creep | Invoicing only: KNIGHT records invoices and observed payments and moves no money ([`adr/0019`](adr/0019-entitlement-as-an-explicit-record.md)). A payment gateway remains a separate, later decision |
| R15 | Single-developer bandwidth across .NET + Django + React + agent | Everything progresses, nothing finishes | Follow the phase order in `TODO.md`; do not start a phase before the previous one's exit criteria |
| R16 | **KNIGHT delivers executable code into customer production systems** | A bug or compromise in delivery can break or breach every store at once | Signed artifacts, digest pinned in KNIGHT, typed job vocabulary, kill switch on publishing, and — built in phase 10 — **staged rollouts**: the canary is one store, no wave starts before the last one reports, a failed canary halts the rollout whatever the threshold says ([`adr/0028`](adr/0028-staged-rollouts-with-a-single-store-canary.md)). The one part still outstanding is the **external review of the delivery path** |
| R17 | Remote Django migrations on customer databases | Data loss or a half-migrated store | Expand/contract mandatory, declared reversibility, restore point, honest `ManualInterventionRequired` outcome ([`adr/0016`](adr/0016-feature-migration-and-removal-policy.md)) |
| R18 | Dependency/compatibility resolver complexity (diamonds, ranges, yanks) | Wrong plans, blocked installs, or bad installs | Resolve centrally before job creation, extensive unit tests, dry-run `plan` endpoint, refuse rather than guess |
| R19 | Feature ↔ store version matrix grows combinatorially | Untestable surface | Narrow supported store-version ranges, a compatibility test matrix in CI, deprecate old store versions deliberately |
| R20 | Feature installations drift from KNIGHT's record (manual edits on a server) | KNIGHT's view becomes fiction | Periodic reconciliation, `feature.drift` alert, store reports its true installed set on every health check |
| R21 | ~~Signing key custody and rotation are undefined~~ **Resolved in phase 3.5** | Compromise cannot be contained | Ed25519 detached signatures behind an `ISigner` abstraction: file/environment-backed in development and CI, with a documented path to a cloud KMS or HSM. Every `FeatureVersion` records the `signingKeyId` that signed it, and that column is indexed, so revoking a key means yanking everything it ever signed in one query |
| R22 | Agent privilege on customer servers | Highest-value target in the system | Least privilege, no shell, signed agent releases, auditable job history, per-store scoping |
| R23 | Feature packages could drift toward becoming microservices | Operational explosion the spec forbids | Explicit rule in [`adr/0014`](adr/0014-features-as-deployable-packages.md); a network service requires its own ADR |
| R24 | Delivery scope may dominate the roadmap | Core control plane slips | Phase 3.5 is scoped to a single reference feature end to end before breadth |
| R25 | File-backed signing keys are accepted for the first release | A compromise of the API host becomes arbitrary code on every store it manages — the highest-value key in the system | **Accepted 2026-08-20** by the product owner, deliberately and with the consequence understood. Conditions: the private key lives outside the repository, the host is hardened, and the position is revisited before the first customer who is not the company itself. The `ISigner` abstraction and the indexed `signingKeyId` mean moving to a KMS is a hosting change rather than a code change, and revoking a compromised key yanks everything it signed in one query |
<<<<<<< HEAD
=======
| R26 | The Feature manifest is bound to Django while a store may be any stack | A capability sold as a tier cannot be delivered to a non-Django store, and the gap is invisible until somebody tries | The wire contract, the job vocabulary and the step names are all runtime-neutral already, so the binding is one file: `ManifestReader` refuses a manifest with no `django:` block and validates `app_label` and `installed_app` as Python identifiers. Until the decision in section 3 is taken, a non-Django store is entitled by KNIGHT and enforces those entitlements itself, which is a supported arrangement rather than a workaround ([`connecting-a-store.md`](connecting-a-store.md) section 8) |
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5

## 3. Decisions still needed from the product owner

1. ~~**R1** — is there any real customer data in the current schema?~~ **Resolved:** no. See R1.
2. **Billing** — does KNIGHT need to take payments, or only issue invoices?
3. **Currency and tax** — IRR/Toman only, or multi-currency? Tax rules?
4. **Log shipping** — is centralised log ingestion in scope for the first
   release, or is error aggregation enough?
5. **Agent hosting** — will customer-managed servers be supported in the first
   release, or only company-managed ones?
6. **Locale** — Persian-only UI, or Persian + English from the start? (docs
   assume both, with Persian default)
7. **Store provisioning** — how much is automated in the first release?
   (`store-provisioning.md` §2 proposes: store record, credentials, agent, and
   base Feature installation automated; VM/DB/TLS creation manual at first)
8. ~~**Package registry**~~ **Resolved:** object storage with KNIGHT as the
   index. KNIGHT already owns the version records, digests and signatures, so a
   separate index service would be a second source of truth to keep in step.
   Artifacts live in S3-compatible storage — MinIO locally — and an agent
   fetches one through a short-lived signed URL minted per job, never a stored
   URL.
9. ~~**Signing key custody**~~ **Resolved:** see R21.
10. ~~**First feature**~~ **Resolved:** `knight-feature-analytics-core` and
    `knight-feature-analytics-reports`, the second depending on the first. Two
    features rather than one because dependency resolution is the part of the
    phase most likely to be wrong, and a single feature never exercises it
    against a real package.
11. **Uninstall data policy** — default retention window after uninstall, and
    whether customers may request immediate purge.
12. ~~**Email delivery**~~ **Resolved 2026-08-20:** the email channel stays as
    it is — it refuses honestly rather than reporting a message delivered that
    went nowhere — and SMTP is wired in phase 9, where the mail host and its
    credentials are chosen alongside the rest of the deployment. Webhook and
    in-app channels carry alerting until then.
13. **Pre-release verification** — *resolved in phase 10, by building it rather
    than by a decision.* The proposal was that **a restore drill for the KNIGHT
    database should be the one thing blocking a release**. That drill now exists
    and runs in CI on every push, so it is a standing gate instead of a
    ceremony performed once: it takes a real backup, restores it into a scratch
    database and compares the table list, every row count, the migration history
    and the constraints and indexes
    ([`adr/0027`](adr/0027-the-restore-drill-is-the-backup-test.md),
    [`runbooks/restore-drill.md`](runbooks/restore-drill.md)).

    The load test on ingestion and delivery was also done in phase 10 rather
    than deferred, and its numbers are in
    [`phase-10-verification.md`](phase-10-verification.md). The **external
    review of the delivery path remains outstanding and is the one item here
    nobody inside the project can close** — its scope is prepared in
    [`security/external-review-scope.md`](security/external-review-scope.md).
    A full provisioning run against a real provider still cannot happen until
    the provider integration exists, so launching with stores registered by hand
    remains the assumption unless somebody says otherwise.

<<<<<<< HEAD
=======
14. **Runtime-neutral Feature manifests** — should a Feature be publishable for
    a store that is not a Django application? Everything except the manifest is
    already stack-agnostic: the ingestion contract is plain HTTP, and the job
    vocabulary is a closed list of names a store carries out however its runtime
    does. Only [`ManifestReader`](../backend/modules/FeatureRegistry/Domain/ManifestReader.cs)
    insists on a `django:` block, so today a Feature cannot be published for such
    a store at all.

    This is a product question rather than a technical one. Answering *yes* means
    a `runtime:` discriminator with a per-runtime block, a schema change, and a
    packaging format per runtime — real work, and it widens what the delivery
    path can install, which is the highest-risk surface in the system (R16).
    Answering *no* is equally legitimate: non-Django stores stay entitled and
    observed, and deploy their own code. What is not legitimate is leaving it
    implicit, because the docs currently say a Feature is never a flag
    ([`adr/0014`](adr/0014-features-as-deployable-packages.md)) while a
    non-Django store has nothing else available to it.

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
## 4. Things deliberately not being done yet

Kubernetes · message broker · service extraction · event sourcing · custom log
storage · AI analysis features · payment processing · multi-region · features
as network services · KNIGHT building feature code at delivery time · full
per-store CI/CD rebuilds. Each needs a written justification and an ADR before
it enters the codebase.
