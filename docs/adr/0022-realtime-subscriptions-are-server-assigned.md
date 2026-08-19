# 0022 — Realtime subscriptions are assigned by the server, never requested

- Status: **Accepted**
- Date: 2026-08-19

## Context

Phase 5 adds a realtime channel so the dashboard can show alerts, incidents and
notifications as they happen rather than when somebody reloads. SignalR is the
transport ([`adr/0011`](0011-frontend-architecture.md) already chose the stack).

The question this record settles is not which transport, but **who decides what
a connection receives**. The obvious design — a `Subscribe(customerId)` hub
method the client calls after connecting — is the one almost every tutorial
shows, and it is wrong here.

KNIGHT's isolation guarantee is enforced at the persistence layer by a global
query filter that fails closed (`docs/authorization.md` §3). SignalR groups are
not queries. Nothing in that filter, and nothing anywhere else in the system,
would notice a customer principal calling `Subscribe` with a neighbour's
customer id — and what leaks is live operational detail about someone else's
stores: their error messages, their incidents, their outages.

## Options considered

1. **A `Subscribe(customerId)` hub method, with a server-side check.** Workable,
   but it puts an authorization decision in a place where forgetting it produces
   no error, no failing test and no log line — only a silent cross-customer leak.
   Every future hub method would have to remember the same check.
2. **A subscription token minted per customer.** Correct, and more machinery than
   the problem needs: another secret to issue, rotate and revoke.
3. **Groups assigned on connect from the connection's own claims.**

## Decision

**Option 3.** There is no hub method a client can call to choose what it
receives, and there will not be one.

`OnConnectedAsync` reads the authenticated principal and joins the connection to
its groups itself:

- a customer principal joins exactly one group, its own;
- a platform principal joins the platform group;
- a connection whose second factor is still outstanding is aborted, matching the
  rule the permission handler already applies to every endpoint.

Broadcast routing mirrors the persistence filter exactly: a message about one
customer reaches that customer and platform staff; a message with no customer is
platform infrastructure and reaches platform staff only. "No customer" is never
read as "everyone".

Two consequences fall out of this that are worth stating, because both were
found by running it rather than by reasoning about it:

- **Claims are read from the hub's own `Context.User`, never from
  `IHttpContextAccessor`.** Once a WebSocket has upgraded there is no
  `HttpContext` left, so a request-scoped principal reports every connection as
  anonymous — which is indistinguishable from an attack and silently kills every
  connection.
- **The bearer token is accepted from the query string for the hub path only.**
  A browser cannot set an `Authorization` header on a WebSocket or an
  `EventSource`, so SignalR puts the token in the query. Accepting query-string
  tokens on ordinary endpoints would put credentials into every proxy log and
  browser history entry that saw the URL.

## Consequences

**Positive** — the leak described above is not merely prevented but
unrepresentable: there is no API through which a client can name a group.
Adding a hub method later cannot weaken it, because routing is not derived from
anything a client sends. The rule is the same one the query filter applies, so
there is one isolation model to reason about rather than two.

**Negative** — a client cannot narrow what it receives, so a platform operator's
connection carries every customer's events. That is acceptable at KNIGHT's scale
and is the safe direction to be wrong in; if volume ever makes it a problem, the
fix is server-side filtering by claims, not client-chosen subscriptions.

**Also** — realtime is an improvement on polling and never something correctness
depends on. Every screen fetches its own data and stays correct if the channel
never opens; a push only tells a list that it is stale. A broadcast that fails is
logged and dropped, never allowed to fail the operation that produced it.
