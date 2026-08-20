#!/usr/bin/env bash
#
# Takes a backup of the KNIGHT control-plane database.
#
# Custom format (-Fc) rather than plain SQL, for three reasons: it is
# compressed, pg_restore can read it selectively when only one table needs to
# come back, and it can be restored in parallel. The cost is that the file is
# not human-readable, which is why the manifest written next to it records in
# plain text what the file contains.
#
# The dump is of one database, not the cluster. KNIGHT's roles and the store
# databases are deliberately out of scope: roles are configuration and belong to
# the deployment, and a store's database is the store's own responsibility
# ([`adr/0026`](../../docs/adr/0026-knight-records-backups-it-does-not-take-them.md)).
#
# Usage:
#   PGHOST=... PGPORT=... PGUSER=... PGPASSWORD=... \
#   infrastructure/scripts/knight-backup.sh [output-directory]
#
# Environment:
#   KNIGHT_DB            database to dump               (default: knight)
#   KNIGHT_BACKUP_DIR    where dumps go                 (default: ./backups)
#   KNIGHT_BACKUP_KEEP   how many dumps to keep         (default: 14)

set -euo pipefail

DB="${KNIGHT_DB:-knight}"
OUT_DIR="${1:-${KNIGHT_BACKUP_DIR:-./backups}}"
KEEP="${KNIGHT_BACKUP_KEEP:-14}"

command -v pg_dump >/dev/null || {
  echo "pg_dump is not on PATH. Run this inside the postgres:17-alpine image if the host has no client tools." >&2
  exit 1
}

mkdir -p "$OUT_DIR"

# UTC, and sortable. A backup directory that sorts lexically is a backup
# directory an operator can read under pressure.
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
BASE="knight-${DB}-${STAMP}"
DUMP="${OUT_DIR}/${BASE}.dump"

echo "Dumping '${DB}' from ${PGHOST:-localhost}:${PGPORT:-5432} ..."

# --no-owner and --no-privileges: the restore target is a drill database or a
# rebuilt server, and neither is guaranteed to have the same role names. Ownership
# is deployment configuration, not data, and insisting on it is the single most
# common reason a restore fails at the moment it is needed.
pg_dump \
  --dbname="$DB" \
  --format=custom \
  --compress=9 \
  --no-owner \
  --no-privileges \
  --file="$DUMP"

SIZE="$(wc -c < "$DUMP" | tr -d ' ')"

# The checksum is the point of the manifest. A dump that decompressed wrong, or
# was truncated by a full disk, is indistinguishable from a good one until the
# restore fails — and by then it is the only copy anyone is looking at.
if command -v sha256sum >/dev/null; then
  SHA="$(sha256sum "$DUMP" | cut -d' ' -f1)"
else
  SHA="$(shasum -a 256 "$DUMP" | cut -d' ' -f1)"
fi

cat > "${OUT_DIR}/${BASE}.manifest" <<MANIFEST
database:   ${DB}
host:       ${PGHOST:-localhost}:${PGPORT:-5432}
taken_at:   ${STAMP}
format:     pg_dump custom (-Fc), compress 9, --no-owner --no-privileges
file:       ${BASE}.dump
size_bytes: ${SIZE}
sha256:     ${SHA}
pg_dump:    $(pg_dump --version)
MANIFEST

echo "Wrote ${DUMP} (${SIZE} bytes)"
echo "sha256 ${SHA}"

# Retention last, and only after the new dump is safely written. Pruning first
# would mean a failed dump leaves fewer backups than it found.
if [ "$KEEP" -gt 0 ]; then
  mapfile -t OLD < <(ls -1t "${OUT_DIR}"/knight-"${DB}"-*.dump 2>/dev/null | tail -n +"$((KEEP + 1))")
  for stale in "${OLD[@]:-}"; do
    [ -n "$stale" ] || continue
    echo "Pruning $(basename "$stale")"
    rm -f "$stale" "${stale%.dump}.manifest"
  done
fi

echo "$DUMP"
