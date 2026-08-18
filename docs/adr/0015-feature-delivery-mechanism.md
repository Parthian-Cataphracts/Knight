# 0015 — Feature delivery via agent-pulled jobs and signed artifacts

- Status: **Accepted**
- Date: 2026-08-18
- Depends on: [`0014`](0014-features-as-deployable-packages.md)

## Context

KNIGHT must install a Feature version into a store's Django application,
across networks it does not control, without becoming a remote shell and
without downloading unauthenticated code.

## Options considered

1. **KNIGHT pushes over SSH** — needs inbound access and privileged
   credentials for every server; effectively a remote shell; rejected.
2. **KNIGHT calls a store HTTP endpoint that performs the install** — requires
   inbound reachability (impossible behind NAT for some customers) and puts
   privileged lifecycle operations in the web process; rejected.
3. **Agent pulls narrowly-typed jobs from KNIGHT and executes them.**
4. **Full CI/CD per store** (rebuild and redeploy the store image on purchase)
   — cleanest immutability story, but requires KNIGHT to own every store's
   build and deploy pipeline, including customer-managed servers. Deferred as a
   future option for `DedicatedManaged` stores.

## Decision

**Option 3.** The agent (or the store integration layer where no agent exists)
polls KNIGHT over an outbound HTTPS connection for jobs addressed to its store,
and executes only the fixed set of typed operations in `feature-delivery.md` §7:
`Install`, `Upgrade`, `ApplyConfiguration`, `Enable`, `Disable`, `Uninstall`,
`Rollback`, `HealthCheck`.

Controls:

- Jobs carry a **type and typed parameters**, never a command string. Anything
  unrecognised is rejected by the agent, not executed.
- Artifacts come from a KNIGHT-controlled private package registry over TLS;
  the agent verifies the **sha256 digest and detached signature** from the
  `FeatureVersion` record before touching them. Unsigned or mismatched
  artifacts are refused and reported.
- The job token is short-lived, single-job, and scoped to one store.
- The agent runs with least privilege: it may install into the store's
  virtualenv, run the store's `manage.py` for declared operations, and reload
  the service. It has no general shell, no arbitrary file access, no inbound
  port.
- Progress and results are pushed back per step; every step is audited.
- One job at a time per store; jobs are idempotent and bounded by timeout and
  retry policy.

## Consequences

**Positive** — works behind NAT and on customer-managed infrastructure; no
inbound attack surface on stores; code authenticity is verifiable; the blast
radius of a compromised KNIGHT is limited to a fixed operation vocabulary.

**Negative** — polling latency (mitigated by long-poll or a short interval);
the agent becomes a privileged, security-critical component that must be
signed, versioned, and updatable; a compromised agent still affects its own
store, so agent tokens and provisioning must be tightly controlled.

**Follow-up** — decide the package registry implementation (private PyPI vs
object storage with an index) and the signing key custody model; both are
tracked in `risks.md`.
