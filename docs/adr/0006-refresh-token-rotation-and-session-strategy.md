# 0006. Refresh Token Rotation and Browser Session Strategy

## Status

Accepted

## Context

Phase 00/01 established JWT access tokens and a minimal `RefreshToken`
persistence shape, but no actual login/refresh/logout lifecycle. Building
that lifecycle required deciding how refresh tokens are represented,
transported, rotated, and revoked, and how that interacts with the existing
Platform/Tenant context separation. The system will eventually run on
multiple application instances, so any "only one valid at a time" guarantee
has to hold at the database level, not in application memory.

## Decision

- **Opaque, not JWT.** Refresh tokens are 256 bits of cryptographically
  random data, base64url-encoded for transport (`RefreshTokenGenerator`).
  Only their SHA-256 hash is ever persisted (`RefreshToken.TokenHash`); the
  raw value exists only in the response cookie at issuance.
- **Rotation families.** Every login starts a family (`FamilyId`) with one
  absolute expiration (`ExpiresAt`) shared by every token rotated within it —
  rotation never extends a family's lifetime, only replaces which token in it
  is currently valid.
- **Atomic single-use rotation.** Consuming a token and issuing its successor
  is one conditional `UPDATE ... WHERE ConsumedAt IS NULL AND RevokedAt IS NULL`
  inside a database transaction (`IRefreshTokenRepository.TryConsumeAndIssueAsync`).
  PostgreSQL's row lock is the atomicity boundary: of two concurrent requests
  presenting the same token, at most one can affect a row.
- **Reuse detection, conservative response.** Presenting an already-consumed
  or already-revoked token — whether genuine attacker replay or a benign
  lost race between two legitimate concurrent requests — revokes the entire
  family and denies the request. The system does not attempt to distinguish
  the two cases; both require reauthentication. This is a deliberate
  trade-off: a stricter "graceful winner" design that lets one of two racing
  legitimate requests survive is more complex and would also make genuine
  replay harder to reason about, for a scenario (two truly simultaneous
  refreshes of the same session) that is rare in practice.
- **Short-lived JWT access tokens**, issued fresh alongside each successful
  login/refresh, kept deliberately short (5 minutes Platform, 10 minutes
  Tenant) so that logout/revocation never needs a database lookup on every
  authenticated request — only future refreshes are prevented; an
  already-issued access token simply expires.
- **HttpOnly cookie transport**, one name/path pair per principal type
  (`/api/platform/auth`, `/api/tenant/auth`), never returned in a JSON body.
- **Same-origin future deployment assumption.** Independently-domained tenant
  frontends will need a reverse proxy or BFF putting the API on the same
  origin, so refresh cookies stay first-party. That proxy is out of scope
  now; cookie security is not weakened to compensate for its absence.

## Consequences

- Reuse detection and concurrent-rotation safety are proven against real
  PostgreSQL, not assumed — see `Knight.IntegrationTests.Auth.ReuseAndConcurrencyTests`.
- A legitimate double-submit race (e.g. a flaky client retry) results in
  forced reauthentication, not a silently recovered session. This is an
  accepted UX cost for the simpler, more conservative security property.
- No server-side access-token blacklist exists; a compromised access token
  remains valid for up to its short lifetime even after logout. This is
  intentional (see `docs/architecture/authorization.md`, "Access token
  revocation") and must be revisited if a future requirement needs
  faster-than-lifetime access-token invalidation.
- Multiple application instances can safely serve refresh rotation
  concurrently without coordination beyond the database itself.
