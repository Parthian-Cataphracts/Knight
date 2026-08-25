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
# Environment:
#   standard PG* variables for the connection
#   KNIGHT_ADMIN_PSQL   command allowed to drop and create a database, for
#                       example "runuser -u postgres -- psql". Defaults to psql
#   KNIGHT_DB_OWNER     role that should own the recreated database

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

# Dropping and creating a database needs a privilege the application role does
# not have, and should not be given: it owns one database and has no business
# creating others on a machine it may be sharing. KNIGHT_ADMIN_PSQL names the
# command that runs those two statements - typically "runuser -u postgres --
# psql" on a host with a local cluster. Unset, it is the same psql as
# everything else, which is right for the CI drill where the role owns the
# cluster.
if [ -n "${KNIGHT_ADMIN_PSQL:-}" ]; then
  # Deliberately split on whitespace: the value is a command with arguments.
  # shellcheck disable=SC2206
  ADMIN_PSQL=(${KNIGHT_ADMIN_PSQL})
else
  ADMIN_PSQL=(psql)
fi

# Who owns the recreated database. It matters as soon as the two statements
# above run as somebody else: a database owned by the superuser is one the
# application role cannot restore into, let alone use.
OWNER="${KNIGHT_DB_OWNER:-}"

EXISTS="$("${ADMIN_PSQL[@]}" --dbname=postgres -tAc "select 1 from pg_database where datname = '${TARGET}'")"

if [ -n "$EXISTS" ] && [ "$FORCE" != "--force" ]; then
  echo "Database '${TARGET}' already exists. Pass --force to drop and recreate it." >&2
  exit 1
fi

if [ -n "$EXISTS" ]; then
  echo "Dropping existing '${TARGET}' ..."
  # Sessions still attached would make the drop fail; a restore that stops here
  # because a forgotten psql is open is a restore that has not happened.
  "${ADMIN_PSQL[@]}" --dbname=postgres -q -c \
    "select pg_terminate_backend(pid) from pg_stat_activity where datname = '${TARGET}' and pid <> pg_backend_pid()" >/dev/null
  "${ADMIN_PSQL[@]}" --dbname=postgres -q -c "drop database \"${TARGET}\""
fi

echo "Creating '${TARGET}' ..."
if [ -n "$OWNER" ]; then
  "${ADMIN_PSQL[@]}" --dbname=postgres -q -c "create database \"${TARGET}\" owner \"${OWNER}\""
else
  "${ADMIN_PSQL[@]}" --dbname=postgres -q -c "create database \"${TARGET}\""
fi

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
