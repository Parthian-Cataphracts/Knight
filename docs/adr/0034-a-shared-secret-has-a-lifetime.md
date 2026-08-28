# 0034 — A shared secret is a row with a lifetime, and KNIGHT issues it

**Status:** accepted
**Extends [`0033`](0033-api-driven-features.md).**

## Context

`adr/0033` settled that a Feature may be a service the store talks to, and that
the store proves it is the store by signing every request with a shared secret.
It did not say where that secret comes from, and the answer, until now, was: an
operator typed it into two places — an environment variable on the store and a
column on the service.

That is correct for one store and one service. It becomes an incident at ten,
for four reasons that are each independent of the others:

- **Nobody can rotate it.** The service held one secret per store in a column.
  Writing a new value refused, in the same instant, every request that had been
  signed with the old one and was still in flight. A rotation was therefore an
  outage, so no rotation ever happened, so the secret in use was the one typed
  in on the first day — which is the worst property a credential can have.
- **Nobody can say who holds it.** A value in an environment variable and a
  column has no issue date, no expiry and no record of having been replaced.
  "When did this key start working" and "is this the one we issued" were both
  unanswerable.
- **A store had to exist on the service before it could talk to it**, and the
  only way to put it there was an operator running `knight_store add` on the
  service's host. Ten stores is ten manual steps on somebody else's machine,
  each of which is a chance to paste the wrong secret into the wrong shop.
- **Withdrawing an entitlement did not reach the service.** The store stops
  forwarding, because its own registry says the Feature is disabled. The
  service kept answering, because nothing told it. A store with a stale
  registry — or one restored from a backup taken before the withdrawal — could
  still reach a Feature nobody was paying for.

The last one is the serious one. Entitlement is enforced server-side by rule,
and "server-side" was being satisfied by the *store's* server, which is the one
server in this architecture that KNIGHT does not operate.

## Decision

**A shared secret is a row with a lifetime, and KNIGHT is what issues it.**

1. **The service stores secrets as rows, not a column.** A store has a *set* of
   currently usable secrets: each has a `valid_from`, an optional `expires_at`,
   and a `revoked_at` for the ones that were ended rather than aged out. A
   request verifies if it matches **any** currently usable secret.

2. **Rotation overlaps.** Issuing a new secret sets an expiry on the previous
   ones rather than deleting them, so both verify for the length of one window.
   A request signed a second before the change is still good a second after it,
   which is what makes rotating a secret a deploy rather than an outage. An
   overlap of zero is allowed, because that is what a leak needs, and it is not
   the default.

3. **An expiry only ever moves downwards.** A second rotation with a longer
   window must not give an already-expiring secret more life.

4. **KNIGHT authenticates as KNIGHT.** The service has a second caller with its
   own credential — not a store's — over its own route prefix. A store cannot
   prove it is a store before it has a secret, and issuing that secret is
   exactly what these routes do, so authenticating them as a store would be
   circular. The control-plane surface is refused entirely when no control
   secret is configured: unconfigured is closed, never open.

5. **Nothing reads a secret back.** The control plane writes them and reports
   how many are live; there is no route that returns a value. KNIGHT holds its
   own copy, and a second place to read one from is a second place to steal one
   from.

6. **Revocation is the service's own fact.** Withdrawing an entitlement disables
   the store's registration *and* ends its secrets, so a store whose registry is
   stale, wrong or restored from a backup is refused by the service rather than
   trusted to refuse itself.

## Consequences

**A secret can be changed, so it will be.** The property that made rotation
theoretical is gone; what remains is a control plane that has to actually do it,
on a schedule, which is operational work rather than a design problem.

**Two secrets are valid at once, for a window.** That is a deliberate widening
of the attack surface, bounded by the window and by the fact that both values
are held in the same two places the single one was. The alternative — no
overlap — was measured against a rotation that never happens, and a credential
nobody rotates is worse than one that is briefly double-valid.

**The service has a second credential to protect**, and it is the strongest one
in the system: whoever holds it can issue a secret for any store. It is one
value, held by the control plane, never by a store, and it opens nothing that
serves a store's data — the control-plane routes read and write registrations,
and no subscription is reachable through them.

**Registration is no longer an operator action.** `knight_store add` remains for
an operator fixing something by hand, and it now performs a rotation rather than
an overwrite, so using it does not cut off whatever is in flight.

**A store restored from an old backup stops working rather than working
wrongly.** Its secrets have moved on. That is the intended outcome and it needs
to be said out loud, because the failure looks like a broken store to whoever
finds it.
