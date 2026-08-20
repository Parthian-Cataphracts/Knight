# 0027 — The restore drill runs in CI, and it is the only thing that counts as testing a backup

- Status: **Accepted**
- Date: 2026-08-20

## Context

[`deployment.md`](../deployment.md) §10 asks for "nightly `pg_dump` of the KNIGHT
database with restore drills documented", and
[`risks.md`](../risks.md) §3 question 13 proposes the restore drill as the one
thing that should block a release. Everything KNIGHT knows — customers, stores,
credential hashes, entitlements, the audit trail, the feature registry — lives in
one PostgreSQL database. Nothing else in the product survives losing it.

The problem with backups is not that people forget to take them. It is that a
backup which has never been restored is indistinguishable from one that works:

- `pg_dump` can write a truncated file if the disk fills, and exit non-zero in a
  cron job nobody reads.
- `pg_restore` reports errors and **still exits 0** unless `--exit-on-error` is
  passed, so a half-restored database looks like a successful restore.
- A dump can be of the wrong database, or of a schema no deployed version of the
  application can run against.
- Data can restore while a unique index does not, which no row count reveals and
  which corrupts quietly from the first duplicate write afterwards.

Each of those is invisible until the morning it matters.

## Decision

**The restore drill is a CI job, not a calendar entry.** It runs on every push,
against a database built the way a deployment builds one.

The drill takes a real backup, restores it into a scratch database, and then
proves the restored copy is the database that was backed up. It compares, in
order: that `pg_restore` completed under `--exit-on-error`; the table list; the
row count of every table, counted exactly rather than estimated; the EF Core
migration history, so the restored schema is a version the application code
knows; and the constraints and indexes. Each check is there because it fails
independently of the others.

Three supporting decisions follow from it:

- **Every dump is written with a manifest** recording its size and SHA-256, and
  `knight-restore.sh` verifies the checksum before it touches anything. CI
  corrupts a dump on purpose and asserts the restore refuses it, because a guard
  that has never refused anything is not known to work.
- **Dumps are taken `--no-owner --no-privileges`.** The restore target is a
  drill database or a rebuilt server, and neither is guaranteed to have the same
  role names. Ownership is deployment configuration, not data, and insisting on
  it is the most common reason a restore fails at the moment it is needed.
- **Migrations are applied by `Knight.Bootstrap --migrate-only`, twice**, and the
  second run must be a no-op. A deploy runs this on every release and most
  releases add no migration, so a seeder that is not idempotent would corrupt
  data during an ordinary deploy rather than during an unusual one.

## Consequences

The release blocker in `risks.md` §3 question 13 is answered by a job that
re-answers it on every commit, rather than by a drill someone performed once.

The drill covers the KNIGHT database only. Store databases stay out of scope for
the reason given in [`adr/0026`](0026-knight-records-backups-it-does-not-take-them.md):
KNIGHT has no connection to them and must not acquire one.

What this still does not prove is a restore onto a *different machine* from a dump
that travelled through object storage, which is the shape a real disaster takes.
Nightly dumps, their offsite copy, and the retention policy for them are
deployment configuration and are named in [`deployment.md`](../deployment.md) §10;
the drill proves the dump and the restore path, not the transport.
