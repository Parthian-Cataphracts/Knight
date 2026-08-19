# Runbook: feature delivery

What to do when a feature installation goes wrong. Written to be followed at
three in the morning by somebody who did not build it.

**First, two facts that stop most panics:**

- A failed installation never affects the store's own business. Features are
  optional apps; a store serves shoppers with a Feature broken or absent.
- Losing an entitlement **disables**, it does not uninstall, and it never deletes
  data. If a customer says their data is gone, that is a bug, not the design.

---

## 1. A job is stuck

**Symptom:** an installation sits in `Pending`, `Installing` or `Updating`, and
the job's `completedStepCount` has not moved.

```
GET /api/v1/jobs?storeId={id}&state=Running
GET /api/v1/jobs/{jobId}        # per-step results, output and error codes
```

The last step with status `Running` is where it stopped.

**KNIGHT recovers this on its own.** The claim has a deadline
(`FeatureDelivery:JobClaimTimeout`, 15 minutes by default), and the sweeper
returns the job to the queue when it lapses, up to `MaxJobAttempts`. Wait one
sweep interval before intervening.

If it has exhausted its attempts, the job is `Failed` with code `job.timeout`.
Then:

1. Check the store is alive at all — `GET /api/v1/stores/{id}/health`. A store
   that stopped heart-beating has a bigger problem than this job.
2. Check the agent is running: `manage.py knight_apply_job` on the store, by
   hand. Its output says exactly what happened.
3. Re-request the install. It is safe: steps are idempotent and the job resumes
   at the first unfinished one.

To stop a job that must not run:

```
POST /api/v1/jobs/{jobId}/cancel   { "reason": "..." }
```

---

## 2. An installation failed

```
GET /api/v1/installations?storeId={id}&state=Failed
```

Read `failureCode` and `rollbackOutcome` before anything else. The rollback
outcome is what decides whether this is routine or serious.

| `rollbackOutcome` | What it means | What to do |
|---|---|---|
| `NotAttempted` | It failed before changing anything. | Fix the cause, retry. |
| `RolledBack` | Everything applied was undone. The store is as it was. | Fix the cause, retry. |
| `PartiallyRolledBack` | Some of it was undone. | Section 3. |
| `ManualInterventionRequired` | An irreversible migration had already applied. | **Section 4. Do not retry.** |

Common `failureCode`s:

| Code | Cause | Action |
|---|---|---|
| `preflight.disk_full` | Not enough room on the store. | Free space; retry. |
| `preflight.too_large` | Artifact above the store's limit. | Raise `KNIGHT_MAX_ARTIFACT_BYTES` or shrink the package. |
| `fetch.failed` | Download failed or the link expired. | Retry. Persistent means the store cannot reach the package store. |
| `digest.mismatch` | Bytes are not what KNIGHT published. | **Do not retry blindly** — section 5. |
| `signature.invalid` / `signature.unknown_key` | Signature did not verify. | **Section 5.** |
| `install.unsafe_archive` | Archive tried to write outside its directory. | **Section 5.** |
| `migrate.failed` | The migration itself failed. | Read the step output; it carries Django's error. |
| `healthcheck.unhealthy` | Installed, but the Feature says it is not working. | Usually a missing dependency or bad configuration. |

---

## 3. Partially rolled back

The package and configuration were restored; something else was not. The store is
in a mixed state.

1. Read the job's steps and find which rollback step failed.
2. Compare what KNIGHT believes against what the store has:
   `GET /api/v1/installations?storeId={id}` versus `installed.json` in the
   store's feature root.
3. Bring them into line deliberately — usually by uninstalling the Feature
   cleanly and reinstalling it.

Do not leave it. A partially rolled back installation is the state where KNIGHT's
record and reality disagree, and every later decision is made from the record.

---

## 4. Manual intervention required

**This is the serious one.** An irreversible migration applied and then a later
step failed. KNIGHT stopped rather than guessing, because guessing here destroys
customer data.

1. **Do not retry the job.** Do not run `migrate` by hand yet.
2. Find the boundary: the job's `migrate` step output names the last migration
   that applied.
3. Get the restore point. The `backup` step recorded what existed before.
4. Decide with whoever owns the data, not alone:
   - **Roll forward** — fix the cause and complete the upgrade. Usually right when
     the migration succeeded and a later step failed.
   - **Restore** — return the store's database to the backup. Loses anything
     written since; only correct if the migration itself corrupted something.
5. Once the store is consistent, reconcile KNIGHT: uninstall and reinstall the
   Feature so the record matches reality.
6. Raise it with the Feature's author. A Feature that reaches this state has
   declared its migrations reversible when they are not, or has an irreversible
   migration that should have been split.

---

## 5. An artifact failed verification

`digest.mismatch`, `signature.invalid`, `signature.unknown_key` or
`install.unsafe_archive` all mean the same thing: **a store was handed code that
is not what KNIGHT published.** Treat it as a security incident until proven
otherwise.

1. **Do not retry, and do not disable the check.**
2. Establish the blast radius:
   ```
   GET /api/v1/features/versions/{versionId}
   ```
   Compare the recorded digest against the artifact in the package store.
3. If they disagree, the package store has been modified. Yank the version and
   everything else signed by that key:
   ```
   POST /api/v1/features/versions/{versionId}/yank
   POST /api/v1/features/signing-keys/{keyId}/revoke
   ```
4. `signature.unknown_key` on its own is usually mundane: a store whose
   `KNIGHT_SIGNING_KEYS` was not updated after a key rotation. Confirm the key id
   is one you actually rotated to before escalating.

The signature is what makes the delivery model safe. The store refusing is the
system working.

---

## 6. Entitled but not installed

The customer pays for it and it is not there.

```
GET /api/v1/installations?customerId={id}&state=NotInstalled
```

`blockingReason` says why, in plain words — an incompatible store version, an
unresolvable dependency, a Feature needing dedicated infrastructure on a shared
store. Nothing is silently skipped.

To see the whole picture without changing anything:

```
POST /api/v1/installations/plan   { "storeId": "...", "slug": "..." }
```

Fix the constraint the plan names, then re-request the install.

---

## 7. Drift

KNIGHT says one thing, the store says another. The store's `installed.json` is
the truth about what is on disk; KNIGHT's record is what it intended.

Causes, in order of likelihood: somebody edited the store by hand; a job's
completion report never arrived; a restore put back an older filesystem.

Resolve towards the store's reality, then bring KNIGHT into line with an explicit
uninstall/reinstall. Never edit `installed.json` by hand — it is written only by
the installer, and hand-editing is how the next install makes a decision from a
file nobody can trace.

---

## 8. Useful commands

```bash
# On the store
python manage.py knight_apply_job          # run any queued job, with output
python manage.py knight_selftest           # can it reach KNIGHT at all
cat "$KNIGHT_FEATURE_ROOT/installed.json"  # what is actually installed

# Against KNIGHT
GET  /api/v1/installations?storeId={id}
GET  /api/v1/jobs?storeId={id}
GET  /api/v1/jobs/{jobId}
GET  /api/v1/audit-logs?targetType=FeatureInstallationJob
POST /api/v1/installations/plan
```
