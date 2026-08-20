# External security review — scope and briefing pack

> **Status: outstanding.** This is the one item in phase 10 that nobody inside
> the project can close. Everything else in the phase was built and verified;
> this needs people who did not write the code. The document exists so that
> engaging a reviewer is a scheduling decision rather than a scoping exercise.

## 1. Why a review, and why of this in particular

KNIGHT is a control plane for stores it does not own, and the single fact that
makes it worth attacking is this:

> **KNIGHT delivers executable code into customer production systems and
> databases.**

That is R16 in [`risks.md`](../risks.md), and it is the largest risk the project
carries. A compromise of the delivery path is not one customer's incident; it is
every customer at once, with attacker-controlled code running inside their
application and their database.

The internal controls are described in
[`security-threat-model.md`](../security-threat-model.md) §4b and are believed
sound. "Believed sound by the people who wrote it" is exactly the claim an
external review exists to test.

## 2. In scope, in priority order

**1. The code-delivery path, end to end.** This is the review. Everything else
below is secondary.

| Stage | Where it lives |
|---|---|
| Building and signing an artifact | [`features/tools/knight_package.py`](../../features/tools/knight_package.py) |
| Publishing a version and recording its digest | `FeatureRegistry`, `POST /api/v1/features/versions` |
| Signature verification at publish | `Knight.Infrastructure/ControlPlane/Security/FeatureArtifactSecurity.cs` |
| Minting a download URL | `IFeatureArtifactStore`, `ArtifactEndpoints` |
| Queueing a job | `FeatureDelivery`, `FeatureDeliveryService` |
| An agent claiming and executing a job | `AgentJobService`, `Knight.Api/Ingest/AgentEndpoints.cs` |
| The store-side installer | [`stores/reference-store/knight_integration/installer/`](../../stores/reference-store/knight_integration/installer/) |
| Verification before install | `installer/verify.py` |
| Staged rollout across the fleet | `FeatureRolloutService`, [`adr/0028`](../adr/0028-staged-rollouts-with-a-single-store-canary.md) |

The questions worth the most reviewer time:

- Can anything become installable without passing signature **and** digest
  verification? Both are checked; the interesting question is whether there is a
  path that reaches an install with only one of them, or with neither.
- Can a job be made to carry an operation outside the typed vocabulary? The
  agent is meant to implement a closed set of steps and refuse everything else
  ([`adr/0015`](../adr/0015-feature-delivery-mechanism.md)). Is the set actually
  closed?
- Can one store's agent obtain a job, an artifact URL, or a credential belonging
  to another store?
- Is a minted artifact URL usable by anyone who obtains it, and for how long?
- What can an attacker who obtains the signing key do that revocation does not
  undo? Key custody is file-backed for the first release (R21/R25) and this is
  known to be the weakest link — the useful output is what it costs.
- Does the staged rollout actually bound blast radius, or can its sequencing be
  skipped by driving the underlying install endpoints directly?

**2. Authentication and session handling.** Store handshake and token issuance
([`adr/0012`](../adr/0012-store-authentication-mechanism.md)), control-plane
sign-in, MFA enrolment, refresh-token rotation and reuse detection.

**3. Customer isolation.** Enforced as a persistence-level filter that fails
closed. `ControlPlaneIsolationTests` is release-blocking; the question for a
reviewer is what it does not cover — in particular the few places that
legitimately call `IgnoreQueryFilters`, which are the deliberate exceptions and
therefore the places a mistake would hide.

**4. The outbound HTTP path (SSRF).** KNIGHT fetches customer-controlled URLs for
domain verification and health polling. `StoreOutboundHttp` and
`IOutboundAddressPolicy` exist to stop that reaching private networks.

**5. Secret handling.** `Redaction` and the audit write path are supposed to make
it impossible for a credential to reach a log, an audit entry or a notification.

## 3. Out of scope

- The reference store's own business logic. It is a sample application; its
  security matters to the review only where the `knight_integration` layer is
  involved.
- Frozen legacy modules — there are none left; phase 8 deleted them.
- Physical and cloud-provider infrastructure. The hosting platform is not chosen
  ([`deployment.md`](../deployment.md) §8), so there is no production
  configuration to review yet. **This is itself worth stating in the report**:
  the review covers the application, not the deployment it does not yet have.

## 4. What the reviewer should be given

- Read access to the repository, including `docs/` — the threat model, the ADRs
  and the risk register are the design intent, and a control that does not match
  its stated intent is a finding even when it is not exploitable.
- A running environment seeded with at least two customers, so isolation can be
  probed rather than reasoned about.
- A platform account and a customer-scoped account, and the fact that
  `feature.publish` is deliberately not held by any customer-scoped role.
- The signing key custody arrangement as it will actually be for the first
  release, not as it is intended to become.

## 5. What we are asking for

A written report, findings ranked by exploitability rather than by CVSS alone,
and for each finding the sentence that matters most: **what an attacker gets**.
Where a finding is a design decision rather than a defect, say so — several of
the decisions above are deliberate trade-offs recorded in ADRs, and a reviewer
disagreeing with one is a useful outcome, not a misunderstanding.

## 6. Definition of done

The review is complete when the report exists, every finding has a decision
recorded against it (fixed, accepted with reasoning, or deferred with a date),
and R16 in [`risks.md`](../risks.md) is updated to say so. Until then R16 stays
open regardless of how much of phase 10 is ticked.
