# ADR 0018 — A separate control-plane context and access module

Status: **accepted** · Date: 2026-08-18 · Phase: 1

## Context

Phase 1 builds the control-plane core: customers, stores, store credentials,
accounts, roles, permissions and the audit trail. Two things already existed and
looked like they could carry that weight:

- `PlatformDbContext`, the single EF Core context for the pre-pivot product,
  with its tenant-scoped global query filter.
- `modules/Identity`, which implements password hashing, access tokens, refresh
  rotation and sessions for `PlatformAdmin` and `TenantUser`.

Both are shaped around the model the pivot moved away from
([`0010`](0010-pivot-to-control-plane.md)): a tenant whose business data KNIGHT
stores and serves. The control plane's model is a customer whose stores KNIGHT
manages and whose business data it must never hold
([`README.md`](../README.md), rules 1 and 3). The store-side modules are frozen
and are removed in phase 8.

The question was whether to extend those two, or to build alongside them.

## Decision

**A separate `ControlPlaneDbContext` on its own `control` schema, with its own
migration history, and a new `AccessControl` module for control-plane identity
and authorization.**

The legacy context and the `Identity` module stay untouched and keep serving the
frozen modules until phase 8 deletes them.

Customer isolation is enforced as a global query filter over
`ICustomerScoped`, derived from a request-scoped `ICustomerScope` that the
pipeline sets from validated claims. It fails closed: an unresolved scope
returns no rows, and a row with no customer is platform-owned and invisible to
customer principals.

## Consequences

**What this buys**

- Phase 8 can drop the store-side tables and the whole legacy context without a
  single control-plane migration being touched or rewritten.
- The isolation filter is written once, for the model that actually needs it,
  rather than layered on top of a tenant filter that means something else.
- `principal_type` is modelled from the start, so store and agent principals
  (phases 3 and 4) slot in without reshaping what already exists.
- Password hashing is still a single decision: the control plane declares its
  own contract and Infrastructure adapts the existing PBKDF2 implementation.

**What it costs**

- Two contexts against one database for the duration of the transition, and two
  migration histories to apply at deployment.
- Two concepts named "Customer" coexist until phase 8 — the control plane's
  paying customer and the store's end consumer. Code that touches both aliases
  the control-plane type explicitly.
- Some duplication between `Identity` and `AccessControl` (lockout, sessions)
  that resolves when the former is deleted.

## Related decisions taken with it

- **Permissions are resolved per request from the database, not read from the
  token.** Revoking a role has to take effect on the next request, not the next
  login ([`authorization.md`](../authorization.md) §6). The resolver caches for
  the lifetime of one request only.
- **The API host does not migrate its own database.** Migrating on startup turns
  every restart into a schema change and makes the host fail to start when the
  database is briefly unreachable. Migration and role seeding are a deployment
  step, run through `tools/Knight.Bootstrap`.
- **No credential is read from configuration.** The first administrator is
  created by hand with a password typed in, masked — the same stance the
  pre-existing bootstrap tool already took, and the reason there is no
  registration endpoint.
- **An MFA-required account that has not enrolled still gets a session**, one
  flagged as not having satisfied the second factor. Authorization then refuses
  every permission-gated endpoint, leaving enrolment as the only thing it can
  reach. The alternative — refusing the login outright — leaves the account with
  no way to enrol at all.
