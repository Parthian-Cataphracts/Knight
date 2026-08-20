# Infrastructure Scripts

Operational scripts for the KNIGHT control plane. All of them take their
database connection from the standard `PG*` environment variables
(`PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`).

| Script | Purpose |
|---|---|
| [`knight-backup.sh`](knight-backup.sh) | Dump the control-plane database and write a manifest with its SHA-256 |
| [`knight-restore.sh`](knight-restore.sh) | Verify a dump's checksum and restore it into a named database |
| [`restore-drill.sh`](restore-drill.sh) | Back up, restore to a scratch database, and prove the two match |

`restore-drill.sh` runs in CI on every push. A backup nobody has restored is not
a backup — see [`adr/0027`](../../docs/adr/0027-the-restore-drill-is-the-backup-test.md).

**How to run them, and what to do when a restore is real:**
[`docs/runbooks/restore-drill.md`](../../docs/runbooks/restore-drill.md).

> A control-plane dump holds every customer record, credential hash and audit
> entry in the system. `.gitignore` excludes `*.dump` and `backups/`; never move
> one somewhere that is not equally protected.
