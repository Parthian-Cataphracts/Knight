# ADR 0020 — Store ingestion: tokens, replay protection, and signed payloads

Status: **accepted** · Date: 2026-08-19 · Phase: 3

Implements [`0012`](0012-store-authentication-mechanism.md), which chose
rotatable credentials plus short-lived tokens but left the mechanics open.

## Context

A store talks to KNIGHT constantly and from a machine nobody in this system
controls. Four questions had to be answered before the first byte was ingested:

1. What does a store present on every call, given that its long-lived secret must
   not be?
2. What stops a captured request from being replayed?
3. What does a store enforce while KNIGHT is unreachable, given that it is the
   store — not KNIGHT — that must refuse to serve a capability nobody paid for?
4. What does KNIGHT run on when Redis is not there?

The fourth is not a hypothetical. Redis was assumed by the existing cache
registration, which meant the API could not start without it, which meant no
developer could run KNIGHT without a container runtime.

## Decision

**A store presents a short-lived token, obtained by proving a credential it
never sends again.**

- `POST /api/v1/ingest/handshake` takes `clientId` + `clientSecret` and returns a
  JWT carrying `principal_type=store`, `store_id`, `customer_id` and the store's
  environment, valid for 30 minutes.
- The token is not revocable, so it is instead made not worth stealing. The
  credential behind it *is* revocable and rotatable, and rotation keeps the
  previous secret usable for a grace window so a running store does not lose
  access the moment an operator clicks the button.
- The store principal carries no permissions at all. What a store may do is
  decided by which endpoints exist for it; a store token is refused by every
  dashboard policy before a handler runs, and a dashboard token is refused by
  ingestion the same way.

**Every refusal looks identical.**

The handshake answers `401` with one body whether the client id is unknown, the
secret is wrong, the credential is revoked, the store is suspended, the customer
is suspended, or the environment does not match. The aggregate runs every check
in a fixed order and hashes a decoy when no credential matched, so an unknown
client id costs the same work as a wrong secret. Which check failed is recorded
in the audit log, where only an operator can read it.

**Replay is prevented by a nonce, and idempotency by a key, over the same
one-shot primitive.**

`IReplayGuard.TryConsumeAsync` returns true the first time a value is seen inside
a window and false afterwards. A handshake may carry a nonce; an ingestion batch
may carry an `Idempotency-Key`. A replayed batch is acknowledged as a duplicate
rather than refused — from the store's point of view it did arrive — while a
replayed handshake is refused outright.

**The entitlement set is signed with a key derived per store.**

A store caches its entitlements and keeps enforcing them while KNIGHT is
unreachable, so it must be able to verify them offline. KNIGHT signs the set with
HMAC-SHA256 under a key derived from a master key and the store's identity, and
hands the derived key back in the handshake response over TLS. Nothing new is
stored, rotating the master key rotates every store's key at once, and a leaked
per-store key is useless against any other store. The master key is separate from
the JWT signing key and required outside Development, so one leak does not
compromise both.

**Redis is optional; the fallback is refused where it would be wrong.**

With `ConnectionStrings:Redis` set, the cache and the replay guard are Redis. With
it empty, both are in-process — correct for a single node, and useless for two,
because one instance cannot see another's nonces. A hosted service refuses to
start a non-Development host in that state, with a message saying why.

## Consequences

**Bought**

- A leaked store token expires in half an hour; a leaked credential is rotated
  from the dashboard without an outage.
- A store keeps enforcing what its customer paid for through a KNIGHT outage,
  and cannot be handed a forged entitlement set by anything that did not complete
  a handshake as that store.
- KNIGHT runs, and its full integration suite runs, on a plain PostgreSQL with no
  container runtime and no Redis.

**Paid**

- Two signing keys to manage rather than one.
- The in-process guard is a real implementation with a real failure mode, kept
  honest only by the startup guardrail. A future deployment that sets
  `ASPNETCORE_ENVIRONMENT=Development` in production would get it silently — the
  guardrail keys off the environment, and nothing can save a host that lies
  about which one it is.
- Timing equality is enforced at the point it matters, not proven globally. A
  future refactor could reintroduce an early return in the handshake path;
  `StoreHandshake` is where that check lives, and it is unit-tested there.

## Alternatives considered

**Sign every request (HMAC over method, path, body, timestamp, nonce) instead of
bearer tokens.** Stronger against a leaked token, and considerably harder for a
store author to get right — a canonicalisation disagreement fails closed and
looks like an outage. KNIGHT signs *its own* outbound requests to stores this way,
where both sides are ours; stores get bearer tokens over TLS.

**mTLS.** The right answer for dedicated stores and recorded as such in
`deployment.md`. It needs certificate distribution and rotation, which is
provisioning's problem (phase 9), and it would have blocked every store on
shared hosting today.

**Push entitlements instead of pulling them.** KNIGHT does push on change, but a
store that was down when the push happened would never know. The pull is what
makes the store's own cache authoritative for its own enforcement.
