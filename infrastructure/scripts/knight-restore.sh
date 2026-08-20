#!/usr/bin/env bash
#
# Restores a KNIGHT backup into a database.
#
# Deliberately refuses to restore over an existing database unless told twice.
# The realistic way to lose a control plane is not a missing backup; it is a
# restore aimed at the wrong database by somebody working at three in the
# morning.
#
# Usage:
#   infrastructure/scripts/knight-restore.sh <dump-file> <target-database> [--force]
#
# Environment: standard PG* variables for the connection.

set -euo pipefail

DUMP="${1:?usage: knight-restore.sh <dump-file> <target-database> [--force]}"
TARGET="${2:?usage: knight-restore.sh <dump-file> <target-database> [--force]}"
FORCE="${3:-}"

command -v pg_restore >/dev/null || { echo "pg_restore is not on PATH." >&2; exit 1; }
[ -f "$DUMP" ] || { echo "No such dump file: $DUMP" >&2; exit 1; }

# Verify the checksum before touching anything. Restoring a corrupt dump over a
# good database turns a recoverable incident into an unrecoverable one.
MANIFEST="${DUMP%.dump}.manifest"
if [ -f "$MANIFEST" ]; then
  EXPECTED="$(awk '/^sha256:/ {print $2}' "$MANIFEST")"
  if command -v sha256sum >/dev/null; then
    ACTUAL="$(sha256sum "$DUMP" | cut -d' ' -f1)"
  else
    ACTUAL="$(shasum -a 256 "$DUMP" | cut -d' ' -f1)"
  fi

  if [ "$EXPECTED" != "$ACTUAL" ]; then
    echo "CHECKSUM MISMATCH for $DUMP" >&2
    echo "  manifest: $EXPECTED" >&2
    echo "  actual:   $ACTUAL" >&2
    echo "Refusing to restore. This dump is not the file the manifest describes." >&2
    exit 1
  fi
  echo "Checksum verified against $(basename "$MANIFEST")."
else
  echo "WARNING: no manifest beside $DUMP; restoring an unverified dump." >&2
fi

EXISTS="$(psql --dbname=postgres -tAc "select 1 from pg_database where datname = '${TARGET}'")"

if [ -n "$EXISTS" ] && [ "$FORCE" != "--force" ]; then
  echo "Database '${TARGET}' already exists. Pass --force to drop and recreate it." >&2
  exit 1
fi

if [ -n "$EXISTS" ]; then
  echo "Dropping existing '${TARGET}' ..."
  # Sessions still attached would make the drop fail; a restore that stops here
  # because a forgotten psql is open is a restore that has not happened.
  psql --dbname=postgres -q -c \
    "select pg_terminate_backend(pid) from pg_stat_activity where datname = '${TARGET}' and pid <> pg_backend_pid()" >/dev/null
  psql --dbname=postgres -q -c "drop database \"${TARGET}\""
fi

echo "Creating '${TARGET}' ..."
psql --dbname=postgres -q -c "create database \"${TARGET}\""

echo "Restoring $(basename "$DUMP") into '${TARGET}' ..."

# --exit-on-error is the whole difference between a restore and the appearance
# of one. Without it pg_restore reports errors and exits 0, and the drill below
# would happily verify a half-restored database.
pg_restore \
  --dbname="$TARGET" \
  --no-owner \
  --no-privileges \
  --exit-on-error \
  --verbose \
  "$DUMP" 2>&1 | tail -n 5

echo "Restored into '${TARGET}'."
