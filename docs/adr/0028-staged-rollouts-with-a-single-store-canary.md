# 0028 — A fleet-wide version change is a staged rollout, and the canary is one store

- Status: **Accepted**
- Date: 2026-08-20

## Context

R16 in [`risks.md`](../risks.md) is the largest risk this project carries:
**KNIGHT delivers executable code into customer production systems**, so a bad
version can break or breach every store at once. The mitigations already in
place — signed artifacts, a digest pinned in KNIGHT, a typed job vocabulary that
contains no "run this command", a kill switch on publishing — all constrain
*what* can be delivered. None of them constrained *how many stores at a time*.

Until phase 10 the only way to move a fleet onto a new version was to install it
store by store. That is not a mitigation; it is the same risk carried out more
slowly, and it depends entirely on whoever is doing it choosing a safe order and
noticing when to stop.

## Decision

A fleet-wide version change is a first-class object: a **rollout**, made of
ordered **waves** of **targets**, and it enforces the safe order rather than
recommending it.

**The canary is exactly one store, never a percentage.** Five percent of four
hundred stores is twenty, and twenty broken stores is not a canary — it is an
incident a smaller first wave would have caught. Given the choice the canary is
a non-production store; where a customer has only production stores, it is one
of those and the operator can see that it is.

**A wave does not begin until the wave before it has reported on every store.**
This is what makes it staged rather than a slower way of installing everywhere.

**A failed canary halts the rollout whatever the failure threshold says.** The
threshold expresses how many failures are tolerable across a fleet; it is not
permission to carry on past the one store that exists precisely to be broken
first. Everything behind the canary is still untouched at that point, which is
the cheapest moment there will ever be to stop.

**Failures are counted across the whole rollout, not per wave.** Three failures
spread one per wave is the same bad version as three in one wave, and only a
whole-rollout count notices the first shape.

**A rollout sequences; it does not install.** Each store's work is an ordinary
upgrade job queued through the existing delivery service and carried out by that
store's agent
([`adr/0015`](0015-feature-delivery-mechanism.md)). A rollout therefore cannot
ask an agent to do anything a hand-made upgrade could not, and the blast radius
of a compromised control plane is exactly what it was before rollouts existed.

**Waves store their store list**, decided when the rollout is planned, rather
than a percentage evaluated later. A rollout that recomputed its targets each
wave would silently change shape when a store was added, removed or suspended
mid-rollout, and "which stores did this version actually reach" is the first
question asked after an incident.

Three consequences of treating a rollout as a plan rather than a transaction:

- **Halting queues nothing further but does not cancel a job already running
  inside a store.** Interrupting a migration half-way is worse than letting it
  finish.
- **Cancelling leaves upgraded stores upgraded.** Automatically downgrading
  working production stores would be a worse outage than the one being avoided.
  Rolling them back is a decision a person makes, per store, with the existing
  rollback job.
- **Resuming keeps the failures that caused the halt** and raises the threshold
  to just past them. Resuming means "I have looked at these and accept them",
  not "pretend they did not happen"; the next failure halts it again.

Rollout routes require `feature.publish`, not `installation.manage`. A rollout
crosses customers and installs code into stores its caller does not own, which is
platform business of the same weight as publishing the version in the first
place. No customer-scoped role holds that permission.

## Consequences

R16 gains the mitigation its entry has always named. The rules are enforced by
the aggregate and covered by tests that are release-blocking for the same reason
the isolation tests are: if "the canary is one store" holds only by convention,
the mitigation does not exist.

At most one rollout of a Feature may be live at a time, enforced by a filtered
unique index. Two concurrent rollouts would race each other onto the same stores
and the loser's jobs would fail against a version the winner had already
installed.

What this deliberately does **not** do is install a Feature into stores that do
not have it. A rollout moves a version forward; putting software into a store
for the first time is an entitlement decision
([`adr/0019`](0019-entitlement-as-an-explicit-record.md)), and folding the two
together would let a version bump quietly install code into stores that never
had it.

Automatic rollback of a halted rollout is not implemented and is not planned
without a measured need. It would mean a failed upgrade triggering an automatic
downgrade with its own migrations across stores that may each be in a different
state — a larger and less predictable operation than the one that failed.
