#!/usr/bin/env bash
#
# The restore drill: take a backup, restore it somewhere else, and prove the
# restored copy is the database that was backed up.
#
# This exists because "we run pg_dump nightly" is not a backup story. The dump
# can be truncated, it can be of the wrong database, it can restore with errors
# that pg_restore reports and then exits 0 on, and none of that is visible until
# the one morning it matters. The drill is the check, and it is the release
# blocker recorded as question 13 in `docs/risks.md`.
#
# What it proves, in order — each check is here because it fails independently
# of the others:
#
#   1. the dump restores at all, with --exit-on-error
#   2. every table present in the source is present in the restore
#   3. every table holds the same number of rows
#   4. the EF Core migration history matches, so the restored schema is a
#      version the application code actually knows how to run against
#   5. constraints and indexes came back, not just the data
#
# Usage:
#   PGHOST=... PGPORT=... PGUSER=... PGPASSWORD=... \
#   infrastructure/scripts/restore-drill.sh
#
# Environment:
#   KNIGHT_DB           the database to drill        (default: knight)
#   KNIGHT_DRILL_DB     the scratch restore target   (default: knight_drill)
#   KNIGHT_BACKUP_DIR   where the dump is written    (default: ./backups)
#   KNIGHT_DRILL_KEEP   keep the scratch DB after    (default: 0 — drop it)

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DB="${KNIGHT_DB:-knight}"
DRILL_DB="${KNIGHT_DRILL_DB:-knight_drill}"
BACKUP_DIR="${KNIGHT_BACKUP_DIR:-./backups}"
KEEP_DRILL="${KNIGHT_DRILL_KEEP:-0}"
SCHEMA="control"

FAILURES=0

note()  { printf '\n=== %s\n' "$*"; }
pass()  { printf '  PASS  %s\n' "$*"; }
fail()  { printf '  FAIL  %s\n' "$*"; FAILURES=$((FAILURES + 1)); }

# Guard against the drill itself being the thing that destroys the database. The
# scratch name must not be the live one, whatever the environment says.
if [ "$DRILL_DB" = "$DB" ]; then
  echo "KNIGHT_DRILL_DB must not be the live database ('${DB}'). The drill drops its target." >&2
  exit 1
fi

note "1. Taking a backup of '${DB}'"
DUMP="$(KNIGHT_DB="$DB" KNIGHT_BACKUP_DIR="$BACKUP_DIR" "${HERE}/knight-backup.sh" "$BACKUP_DIR" | tail -n 1)"
[ -f "$DUMP" ] || { echo "The backup script produced no dump file." >&2; exit 1; }
pass "dump written to $(basename "$DUMP")"

note "2. Restoring into the scratch database '${DRILL_DB}'"
"${HERE}/knight-restore.sh" "$DUMP" "$DRILL_DB" --force
pass "pg_restore completed with --exit-on-error"

# --- The comparisons -------------------------------------------------------

query() { psql --dbname="$1" -tAF'|' -c "$2"; }

TABLES_SQL="select table_name from information_schema.tables
            where table_schema = '${SCHEMA}' and table_type = 'BASE TABLE'
            order by table_name"

note "3. Comparing the table list"
SRC_TABLES="$(query "$DB" "$TABLES_SQL")"
DST_TABLES="$(query "$DRILL_DB" "$TABLES_SQL")"
SRC_COUNT="$(printf '%s\n' "$SRC_TABLES" | grep -c . || true)"

if [ "$SRC_TABLES" = "$DST_TABLES" ]; then
  pass "all ${SRC_COUNT} tables in ${SCHEMA} restored"
else
  fail "table lists differ"
  diff <(printf '%s\n' "$SRC_TABLES") <(printf '%s\n' "$DST_TABLES") || true
fi

note "4. Comparing row counts, table by table"
# Counted with an exact count() rather than reltuples: a planner estimate on a
# freshly restored database that has never been analysed is not a fact, and this
# check exists precisely to catch a table that came back empty.
ROWS_SQL_FOR() {
  printf "select '%s', count(*) from %s.\"%s\"" "$1" "$SCHEMA" "$1"
}

MISMATCHES=0
TOTAL_ROWS=0

while IFS= read -r table; do
  [ -n "$table" ] || continue
  src="$(query "$DB"       "$(ROWS_SQL_FOR "$table")" | cut -d'|' -f2)"
  dst="$(query "$DRILL_DB" "$(ROWS_SQL_FOR "$table")" | cut -d'|' -f2)"

  if [ "$src" != "$dst" ]; then
    fail "${table}: source ${src} rows, restore ${dst} rows"
    MISMATCHES=$((MISMATCHES + 1))
  fi

  TOTAL_ROWS=$((TOTAL_ROWS + src))
done <<< "$SRC_TABLES"

if [ "$MISMATCHES" -eq 0 ]; then
  pass "row counts identical across ${SRC_COUNT} tables (${TOTAL_ROWS} rows total)"
fi

note "5. Comparing the EF Core migration history"
# The restored schema is only useful if the application recognises it. If this
# differs, the restore is a database no deployed version of KNIGHT can run on.
HIST_SQL="select \"MigrationId\" from ${SCHEMA}.\"__ef_migrations_history\" order by \"MigrationId\""
SRC_HIST="$(query "$DB" "$HIST_SQL")"
DST_HIST="$(query "$DRILL_DB" "$HIST_SQL")"
HIST_COUNT="$(printf '%s\n' "$SRC_HIST" | grep -c . || true)"

if [ "$SRC_HIST" = "$DST_HIST" ]; then
  pass "${HIST_COUNT} migrations, identical and in the same order"
  printf '        latest: %s\n' "$(printf '%s\n' "$SRC_HIST" | tail -n 1)"
else
  fail "migration history differs"
  diff <(printf '%s\n' "$SRC_HIST") <(printf '%s\n' "$DST_HIST") || true
fi

note "6. Comparing constraints and indexes"
# Data without its constraints restores and then rots: a unique index that did
# not come back is not visible in a row count, and the first duplicate that gets
# written afterwards is unrecoverable.
CONSTRAINTS_SQL="select conrelid::regclass::text || ' ' || conname
                 from pg_constraint
                 where connamespace = '${SCHEMA}'::regnamespace
                 order by 1"
INDEXES_SQL="select indexname from pg_indexes where schemaname = '${SCHEMA}' order by indexname"

for pair in "constraints:${CONSTRAINTS_SQL}" "indexes:${INDEXES_SQL}"; do
  label="${pair%%:*}"
  sql="${pair#*:}"

  src="$(query "$DB" "$sql")"
  dst="$(query "$DRILL_DB" "$sql")"
  count="$(printf '%s\n' "$src" | grep -c . || true)"

  if [ "$src" = "$dst" ]; then
    pass "${count} ${label} restored"
  else
    fail "${label} differ"
    diff <(printf '%s\n' "$src") <(printf '%s\n' "$dst") || true
  fi
done

# --- Teardown --------------------------------------------------------------

if [ "$KEEP_DRILL" != "1" ]; then
  note "Dropping the scratch database '${DRILL_DB}'"
  psql --dbname=postgres -q -c \
    "select pg_terminate_backend(pid) from pg_stat_activity where datname = '${DRILL_DB}' and pid <> pg_backend_pid()" >/dev/null
  psql --dbname=postgres -q -c "drop database if exists \"${DRILL_DB}\""
else
  note "Keeping '${DRILL_DB}' for inspection (KNIGHT_DRILL_KEEP=1)"
fi

printf '\n'
if [ "$FAILURES" -eq 0 ]; then
  echo "RESTORE DRILL PASSED — ${SRC_COUNT} tables, ${TOTAL_ROWS} rows, ${HIST_COUNT} migrations verified."
  exit 0
fi

echo "RESTORE DRILL FAILED — ${FAILURES} check(s) did not pass." >&2
exit 1
