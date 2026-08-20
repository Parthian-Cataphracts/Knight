# Runbook — backing up and restoring the KNIGHT database

Everything KNIGHT knows lives in one PostgreSQL database. This runbook covers
taking a backup, proving a backup is restorable, and restoring one for real
during an incident.

Why it is a drill and not just a `pg_dump` line in cron:
[`adr/0027`](../adr/0027-the-restore-drill-is-the-backup-test.md).

The scripts are in [`infrastructure/scripts/`](../../infrastructure/scripts/):

| Script | What it does |
|---|---|
| `knight-backup.sh` | Dumps the database and writes a manifest with its SHA-256 |
| `knight-restore.sh` | Verifies the checksum, then restores into a named database |
| `restore-drill.sh` | Backs up, restores to a scratch database, and compares the two |

All three take their connection from the standard `PG*` environment variables.

---

## 1. Prerequisites

`pg_dump` and `pg_restore` must be **at least the version of the server**;
`pg_dump` refuses to dump a server newer than itself. If the host has no client
tools — a Windows workstation, typically — run the scripts inside the PostgreSQL
image instead, as shown in §5.

---

## 2. Taking a backup

```bash
PGHOST=localhost PGPORT=5432 PGUSER=knight PGPASSWORD=... \
KNIGHT_DB=knight KNIGHT_BACKUP_DIR=/var/backups/knight \
infrastructure/scripts/knight-backup.sh
```

Writes two files named for the UTC timestamp:

- `knight-knight-20260820T135452Z.dump` — custom format, compressed
- `knight-knight-20260820T135452Z.manifest` — size, SHA-256, `pg_dump` version

`KNIGHT_BACKUP_KEEP` (default 14) prunes older dumps, and only after the new one
is safely written.

> The dump contains every customer record, credential hash and audit entry in
> the system. Treat the file as the most sensitive artifact the project
> produces. `.gitignore` excludes `*.dump` and `backups/` so one cannot be
> committed by accident.

---

## 3. Running the drill

The drill is what proves a backup is a backup. It runs in CI on every push
(`.github/workflows/backend.yml`, job **Migrations and restore drill**), and can
be run by hand at any time:

```bash
PGHOST=localhost PGPORT=5432 PGUSER=knight PGPASSWORD=... \
KNIGHT_DB=knight KNIGHT_DRILL_DB=knight_drill \
infrastructure/scripts/restore-drill.sh
```

It refuses to start if `KNIGHT_DRILL_DB` equals `KNIGHT_DB`, because it drops its
target. Set `KNIGHT_DRILL_KEEP=1` to keep the restored database for inspection
instead of dropping it at the end.

### Expected output

Recorded from a real run against the development database on 2026-08-20:

```
=== 1. Taking a backup of 'knight'
  PASS  dump written to knight-knight-20260820T135452Z.dump

=== 2. Restoring into the scratch database 'knight_drill'
Checksum verified against knight-knight-20260820T135452Z.manifest.
Dropping existing 'knight_drill' ...
Creating 'knight_drill' ...
Restoring knight-knight-20260820T135452Z.dump into 'knight_drill' ...
Restored into 'knight_drill'.
  PASS  pg_restore completed with --exit-on-error

=== 3. Comparing the table list
  PASS  all 48 tables in control restored

=== 4. Comparing row counts, table by table
  PASS  row counts identical across 48 tables (8767 rows total)

=== 5. Comparing the EF Core migration history
  PASS  12 migrations, identical and in the same order
        latest: 20260820114106_AccountInvitations

=== 6. Comparing constraints and indexes
  PASS  83 constraints restored
  PASS  142 indexes restored

=== Dropping the scratch database 'knight_drill'

RESTORE DRILL PASSED — 48 tables, 8767 rows, 12 migrations verified.
```

The row and table counts differ per environment; what matters is that every
check says `PASS` and the exit code is 0.

### When a check fails

| Failure | What it means | What to do |
|---|---|---|
| `CHECKSUM MISMATCH` | The dump is not the file the manifest describes — truncated, or altered in transit | Do not restore it. Take a fresh backup; if this was the only copy, treat it as data loss and escalate |
| Table lists differ | The dump is of a different schema version than the source | Check `KNIGHT_DB` really names the database you meant |
| Row counts differ | Data did not all come back | The dump is unusable. Investigate before overwriting any good copy |
| Migration history differs | The restored schema is a version no deployed KNIGHT can run against | The dump predates a migration. Restore it, then apply migrations with `--migrate-only` (§4) |
| Constraints or indexes differ | Data restored without its integrity rules | Unusable as-is. A unique index that did not come back corrupts silently from the next write |

---

## 4. Restoring for real

**Under incident conditions.** Read all of this before running anything.

1. **Stop writes first.** Take the API out of service. A restore that runs while
   the application is writing produces a database that matches neither the
   backup nor the live state.

2. **Never restore over the live database.** Restore beside it, verify, then
   swap. `knight-restore.sh` refuses an existing target unless given `--force`,
   and that guard is there for exactly this moment.

   ```bash
   infrastructure/scripts/knight-restore.sh \
     /var/backups/knight/knight-knight-20260820T135452Z.dump \
     knight_recovered
   ```

3. **Verify before swapping.** Point the drill's comparison at the recovered
   database, or at minimum check the migration history and the row counts of
   `customers`, `stores` and `users`.

4. **Bring the schema up to the running code**, if the dump predates the
   deployed version:

   ```bash
   CONTROL_PLANE_DB_CONNECTION_STRING='Host=...;Database=knight_recovered;...' \
   dotnet run --project backend/tools/Knight.Bootstrap -- --migrate-only
   ```

5. **Swap by renaming**, which is atomic and reversible, rather than by
   restoring over the original:

   ```sql
   alter database knight rename to knight_broken_20260820;
   alter database knight_recovered rename to knight;
   ```

   Keep `knight_broken_...` until the incident is closed. It is the only
   evidence of what went wrong.

6. **Rotate what the gap exposed.** Sessions issued after the backup was taken
   no longer exist in the restored database and are already invalid. Store
   credentials issued in that window are *not* in the restored database while
   still being live on the store, so those stores cannot authenticate: re-issue
   their credentials from the dashboard.

---

## 5. Running without local client tools

On a machine with Docker but no PostgreSQL client (a Windows workstation), run
the scripts inside the PostgreSQL image, joined to the database container's
network:

```bash
docker run --rm --network container:docker-postgres-1 \
  -e PGHOST=127.0.0.1 -e PGPORT=5432 -e PGUSER=knight -e PGPASSWORD=knight \
  -e KNIGHT_DB=knight -e KNIGHT_BACKUP_DIR=/work/backups \
  -v "$PWD/infrastructure/scripts:/scripts:ro" \
  -v "$PWD/artifacts:/work" \
  postgres:17-alpine bash /scripts/restore-drill.sh
```

Backups land in `artifacts/backups/`, which is gitignored.

---

## 6. What this does not cover

The drill proves the dump and the restore path. It does not prove a restore onto
a **different machine** from a dump that travelled through object storage, which
is the shape an actual disaster takes. Nightly scheduling, the offsite copy and
its retention are deployment configuration
([`deployment.md`](../deployment.md) §10).

Store databases are out of scope: KNIGHT has no connection to them and must not
acquire one ([`adr/0026`](../adr/0026-knight-records-backups-it-does-not-take-them.md)).
