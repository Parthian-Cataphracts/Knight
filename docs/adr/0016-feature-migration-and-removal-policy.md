# 0016 — Feature migration, rollback, and removal policy

- Status: **Accepted**
- Date: 2026-08-18
- Depends on: [`0014`](0014-features-as-deployable-packages.md)

## Context

A Feature owns Django models, so installing or upgrading it changes a
customer's production database. Two hard questions follow: what happens when an
install fails halfway, and what happens to the data when a customer stops
paying.

Automatic database rollback is frequently unsafe: a dropped column cannot be
restored by re-running a reverse migration, and a data-transforming migration
may be lossy.

## Decision

### Migrations

- A Feature owns migrations only in **its own app label**; it never touches
  store or other Features' tables.
- Manifests declare `migrations.reversible`, an estimated duration, and whether
  a maintenance window is required. KNIGHT surfaces this before the operator
  confirms.
- Migrations are applied by the agent through the store's own `manage.py`
  during a job step, with output captured and streamed as progress.
- **Expand/contract is mandatory** for anything destructive: an upgrade may add
  and backfill; removals happen in a later version, after the previous version
  is no longer in use. This makes most upgrades reversible in practice.
- A backup/snapshot reference is recorded before the migration step. Where the
  infrastructure supports snapshots, one is taken; where it does not, the job
  records that no restore point exists and requires explicit confirmation for
  irreversible operations.

### Rollback

Attempted in reverse order of completed steps:

```
disable → restore configuration → downgrade/remove package
        → reverse migrations ONLY IF declared reversible and present
        → health check → report
```

If a non-reversible migration has already applied, KNIGHT **stops and does not
guess**: installation is marked `Failed` with
`rollbackOutcome = ManualInterventionRequired`, the exact migration boundary is
recorded, a critical incident is opened, and the store is left in the most
functional state available (usually: new schema present, feature disabled).

### Removal

Four distinct operations (`feature-delivery.md` §11): **Disable**,
**Uninstall**, **Rollback**, **Purge**.

**Default on entitlement loss — expiry, downgrade, cancellation — is Disable,
never Uninstall.** Code stays, feature is off, data is retained for the
manifest's `dataRetentionDays`. Renewal is then instant and lossless.
Uninstall is always an explicit, audited action that warns about data
consequences first. Purge is only reached by retention expiry or an explicit
request, and produces an export first.

## Consequences

**Positive** — no silent data loss; honest failure states instead of a
pretend-successful rollback; renewals are trivial; the expand/contract rule
keeps most upgrades genuinely reversible.

**Negative** — disabled-but-installed Features consume storage and must still
be considered during store upgrades and compatibility checks; some failures
require a human, so an operational runbook is required; feature authors carry
real discipline (expand/contract, reversible migrations, own app label) that
must be enforced at publish time by manifest and package validation.
