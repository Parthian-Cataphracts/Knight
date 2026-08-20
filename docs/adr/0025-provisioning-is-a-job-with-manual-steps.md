# 0025 — Provisioning is a job whose manual steps are represented, not hidden

- Status: **Accepted**
- Date: 2026-08-20

## Context

Phase 9 turns [`store-provisioning.md`](../store-provisioning.md) into code.
The flow it describes runs from "a customer signed up" to "a store with its base
Features installed and healthy", and only part of it is automated in the first
release: KNIGHT does not create VMs, does not build the Django instance, and
does not wire DNS or TLS. Those are things a person does today.

That leaves a design question with three plausible answers, and the wrong one is
comfortable.

## Options considered

1. **Model only the automated part.** The job starts once a human has already
   built the machine and the instance, so every step in it is one KNIGHT can
   carry out. Clean, and it makes the run look fully automated. It also means
   the parts most likely to go wrong — a box nobody built, DNS nobody pointed —
   live in somebody's head or a ticket in another system, and a store stuck for
   a fortnight shows as "not provisioned yet" with no indication of who is
   waiting for what.

2. **Automate everything now.** Provider APIs for machines, an image builder,
   a DNS integration. This is the eventual answer for at least some of it, but
   it is a phase of work on its own, and doing it before the state machine has
   ever run would be building the hard half first.

3. **Model the whole flow, and mark each step with who carries it out.** Manual
   steps are real steps: they appear on the run, they hold it in an explicit
   "waiting for a person" state, and an operator completes them with an audited
   action. When automation for one of them lands, the step stops being manual
   and nothing else changes.

## Decision

**Option 3.** A `ProvisioningJob` carries the whole pipeline. Every step
declares a mode — `Automatic` or `Manual` — and:

- an automatic step is evaluated against a fact another module already recorded:
  a usable credential exists, an agent has enrolled on the store's server, the
  store handshaked, its entitled Features installed, its link reached
  `Connected`;
- a manual step is completed by a named operator, recorded with who and when;
- an operator may **never** complete an automatic step. Health check above all:
  a store that has not passed one does not become `Active`, and no amount of
  operator confidence substitutes for the check
  ([`store-provisioning.md` §4](../store-provisioning.md));
- the run has no timer of its own. A coordinator re-evaluates unfinished runs
  periodically, because every fact it waits for happens in another module and
  notifies nobody.

Deprovisioning is the same machine in the other direction — disable, revoke,
stop ingestion, retain, export, purge — with the retention window resolved once,
at the start, from the customer's negotiated override or their plan's promise.

## Consequences

- A stuck store says what it is stuck on, in words an operator can act on:
  "waiting for the agent on the store's server to enrol" rather than "pending".
- The state machine is correct before the automation exists, so automating a
  step later is a change of one evaluator, not a redesign.
- The run is resumable and idempotent by construction: it asks what remains
  rather than replaying from the top, so a restart mid-run never issues a second
  credential or queues a second install.
- The honest cost: a run can sit in `AwaitingOperator` indefinitely. That is a
  true statement about the deployment, and it is visible, which is the point.
