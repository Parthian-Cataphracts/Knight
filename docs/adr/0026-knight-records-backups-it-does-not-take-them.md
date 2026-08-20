# 0026 — KNIGHT records backups; it never takes or holds them

- Status: **Accepted**
- Date: 2026-08-20

## Context

Phase 9 asks for "backup status reporting and `backup.failed` alerting". Backups
are the one operational subject where the temptation to break rule 3 of
[`README.md`](../README.md) is strongest: taking a store's backup requires a
connection to the store's database, and KNIGHT deliberately has none.

## Decision

KNIGHT holds **reports about backups**, never backups and never a route to the
data behind them.

- A store (or the agent beside it) reports to `POST /api/v1/ingest/backups` with
  the status, the window it ran in, the size it produced and a location
  *reference* — a bucket key or volume name, never a credential-bearing URL,
  because the value is shown in the dashboard and kept as long as the report is.
- A report claiming success with no size is **refused**. A backup job whose
  output is zero bytes is the classic silent failure, and recording it as green
  is worse than recording nothing.
- A failed report raises `backup.failed` immediately: the fact is known the
  instant it arrives, and waiting for a sweep to say so buys nothing.
- A separate rule, `backup.overdue`, runs on the observability sweep and finds
  stores nobody has reported a successful backup for. This is the failure that
  needs a timer, because a backup job that was switched off says nothing at all
  and looks identical to a healthy store on every other screen.
- A successful report resolves both alerts. Only a working backup can honestly
  clear "this store's backups are broken".

## Consequences

- A store that never reports is visibly unbacked rather than invisibly unbacked.
  KNIGHT's answer to "is this store backed up?" is "the store last told us on
  Tuesday", which is the strongest true answer available to a control plane that
  cannot see the data.
- Nothing in KNIGHT can restore a store. Restore is the store operator's
  procedure, and the restore drill that phase 10 makes a release blocker is
  about KNIGHT's *own* database, which is a separate promise
  ([`risks.md`](../risks.md) §3 question 13).
- The report path is the ordinary store-authenticated ingest surface, so a
  backup report is subject to the same environment binding and rate limiting as
  every other thing a store says.
