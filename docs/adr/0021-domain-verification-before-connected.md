# ADR 0021 — A store is Connected only once it has proven its domain

Status: **accepted** · Date: 2026-08-19 · Phase: 3

## Context

KNIGHT calls stores. It polls `/api/knight/health` on a schedule, and phase 9
will fetch verification tokens and drive provisioning over the same path. The
address it calls comes from `Store.primaryDomain`, which an operator types into
a form and DNS nobody in this system controls resolves.

A credential proves that whoever is calling holds a secret KNIGHT issued. It
proves nothing whatsoever about who answers on that domain. Those are different
facts, and until phase 3 the model collapsed them: a successful handshake moved
the store straight to `Connected`, at which point KNIGHT would happily start
making authenticated outbound requests to whatever the domain pointed at.

## Decision

**Ownership of the primary domain is proven separately, and the link does not
reach `Connected` until it is.**

- An operator starts verification; KNIGHT issues a single-purpose token and says
  where to publish it: `/.well-known/knight-domain-verification` over HTTP, or a
  TXT record on `_knight-verification.<domain>`.
- KNIGHT fetches the token and compares it in fixed time, exactly, after
  trimming. A page that merely *contains* the token — an error page echoing the
  request — is not proof.
- A store that handshakes successfully with an unproven domain reaches
  `Pending`, not `Connected`, and its handshake response says so. It may ingest:
  ingestion is inbound and authenticated by the credential, and refusing it would
  lose exactly the errors that explain why a store is misconfigured.
- Changing a store's primary domain drops the proof. Ownership is about one
  domain, and carrying it across would let a store verify a domain it controls
  and then point KNIGHT at one it does not.
- Only the HTTP method is implemented. The DNS record is modelled because it is
  how a store with no HTTP surface yet will prove itself during provisioning, and
  offering it before it works would be a switch that cannot be turned on.

**Every outbound call is checked at the socket, not at the hostname.**

A resolved address in a loopback, link-local, private or carrier-grade-NAT range
is refused immediately before connecting, and link-local is refused even when
private ranges are explicitly allowed — that is where cloud metadata services
live. Redirects are not followed, and the response is read under a size cap.
Verification is the bootstrap step and runs unsigned; every call after it carries
an HMAC the store can check, so a store can refuse to describe itself to anyone
but KNIGHT.

## Consequences

**Bought**

- Pointing a store record at an address inside the control plane's network does
  not turn the poller into a request forger.
- `Connected` means something: credentials proven *and* the domain proven.
  `Pending` is now a real state an operator can act on rather than a transient.
- A store's health payload — versions, dependencies, installed features — is not
  readable by anyone who finds the URL.

**Paid**

- Registering a store is a two-step operation, and an operator who stops after
  the handshake has a store that works but reads as `Pending`. The handshake
  response and `knight_register` both say what is outstanding and print the
  token.
- Local development needs the egress policy relaxed, because the reference store
  genuinely is on loopback. That is a configuration switch which defaults to
  refusing, and link-local stays refused regardless.
- A store behind a proxy that rewrites paths cannot serve the well-known path
  without configuration. The DNS method exists for that case and is not
  implemented yet.
