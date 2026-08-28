#!/usr/bin/env bash
#
# Copies the nightly dumps somewhere that is not this machine.
#
# The installer has always ended with a warning that a backup on the same
# machine is not a backup, and that warning was the whole of the offsite story:
# the timer wrote dumps to `${INSTALL_DIR}/backups` and nothing ever moved them.
# A disk failure took the database and every copy of it in one event.
#
# **Where they go is the operator's decision, and this script does not make it.**
# It ships to whatever `KNIGHT_OFFSITE_TARGET` names and refuses to run when it
# names nothing — an offsite job that silently did nothing would be worse than
# no job at all, because the warning would have stopped.
#
# Three destinations, chosen because they cover the three ways this is actually
# done and need nothing installed that a server of this kind does not have:
#
#   rsync://user@host:/path   over ssh, with the host's own key
#   s3://bucket/prefix        any S3-compatible store, through the aws CLI
#   file:///mnt/elsewhere     a mounted volume, which is offsite only if the
#                             mount is — said out loud below, because it is the
#                             one that looks done and often is not
#
# **Every copy is verified.** A dump that arrived truncated is indistinguishable
# from a good one until somebody needs it, so each file's SHA-256 is compared
# against its manifest before it is sent and the remote copy's size is checked
# after. A backup nobody has verified is a promise, not a backup
# (`adr/0027`).
#
# Usage:
#   KNIGHT_OFFSITE_TARGET=rsync://knight@backup.example.com:/srv/knight \
#   infrastructure/scripts/knight-offsite.sh [backup-directory]
#
# Environment:
#   KNIGHT_OFFSITE_TARGET   where dumps go. Required.
#   KNIGHT_BACKUP_DIR       where they come from      (default: ./backups)
#   KNIGHT_OFFSITE_SEND     how many newest dumps to consider  (default: 3)
#   KNIGHT_OFFSITE_DRY_RUN  set to 1 to say what it would do and send nothing

set -euo pipefail

TARGET="${KNIGHT_OFFSITE_TARGET:-}"
SOURCE="${1:-${KNIGHT_BACKUP_DIR:-./backups}}"
SEND="${KNIGHT_OFFSITE_SEND:-3}"
DRY_RUN="${KNIGHT_OFFSITE_DRY_RUN:-0}"

fail() { echo "offsite: $*" >&2; exit 1; }
say()  { echo "offsite: $*"; }

[ -n "$TARGET" ] || fail "KNIGHT_OFFSITE_TARGET is not set. Nothing was copied, and nothing pretended to be."
[ -d "$SOURCE" ] || fail "'${SOURCE}' is not a directory. There is nothing here to copy."

# --- What to send ------------------------------------------------------------
#
# The newest few rather than everything. The point of this job is that the
# recent dumps exist somewhere else; re-uploading a fortnight of them every
# night is bandwidth spent proving something already proved.

mapfile -t DUMPS < <(ls -1t "${SOURCE}"/*.dump 2>/dev/null | head -n "$SEND")

[ "${#DUMPS[@]}" -gt 0 ] || fail "No dumps in '${SOURCE}'. Has knight-backup.sh ever run?"

# --- Verify before sending ---------------------------------------------------

verify() {
  local dump="$1"
  local manifest="${dump%.dump}.manifest"

  [ -f "$manifest" ] || fail "$(basename "$dump") has no manifest. Refusing to ship a dump nothing describes."

  local expected actual
  expected="$(awk '/^sha256:/ { print $2 }' "$manifest")"

  if command -v sha256sum >/dev/null; then
    actual="$(sha256sum "$dump" | cut -d' ' -f1)"
  else
    actual="$(shasum -a 256 "$dump" | cut -d' ' -f1)"
  fi

  [ -n "$expected" ] || fail "$(basename "$manifest") records no checksum."

  # Refused rather than warned. A dump that does not match its own manifest is
  # already the failure this whole arrangement exists to catch, and copying it
  # offsite would turn one bad file into two.
  [ "$expected" = "$actual" ] || fail "$(basename "$dump") does not match its manifest. It was not copied."
}

# --- The three destinations --------------------------------------------------

ship_rsync() {
  local dump="$1" manifest="$2" destination="${TARGET#rsync://}"

  command -v rsync >/dev/null || fail "rsync is not on PATH and KNIGHT_OFFSITE_TARGET names an rsync destination."

  # --partial is deliberately absent: a resumed half-file that keeps its final
  # name is exactly the "arrived truncated" case. Better to send it again.
  rsync --archive --checksum --chmod=F600 "$dump" "$manifest" "${destination%/}/"
}

ship_s3() {
  local dump="$1" manifest="$2"

  command -v aws >/dev/null || fail "the aws CLI is not on PATH and KNIGHT_OFFSITE_TARGET names an S3 destination."

  # Server-side encryption is asked for, never assumed. A bucket that refuses
  # the request tells the operator something worth knowing about that bucket.
  aws s3 cp "$dump" "${TARGET%/}/$(basename "$dump")" --sse AES256 --only-show-errors
  aws s3 cp "$manifest" "${TARGET%/}/$(basename "$manifest")" --sse AES256 --only-show-errors
}

ship_file() {
  local dump="$1" manifest="$2" destination="${TARGET#file://}"

  mkdir -p "$destination"
  install -m 600 "$dump" "${destination%/}/$(basename "$dump")"
  install -m 600 "$manifest" "${destination%/}/$(basename "$manifest")"

  # The one destination that can look finished and not be. A directory on the
  # same disk is a second copy of a file, not a second place.
  say "note: '${destination}' is offsite only if it is a mount of somewhere else."
}

confirm_size() {
  local dump="$1" name expected actual
  name="$(basename "$dump")"
  expected="$(wc -c < "$dump" | tr -d ' ')"

  case "$TARGET" in
    file://*)
      actual="$(wc -c < "${TARGET#file://}/${name}" | tr -d ' ')"
      ;;
    s3://*)
      actual="$(aws s3api head-object --bucket "$(s3_bucket)" --key "$(s3_key "$name")" \
        --query ContentLength --output text 2>/dev/null || echo "")"
      ;;
    *)
      # rsync over ssh: asking the far end costs a second connection, and
      # rsync's own --checksum already refuses to call a mismatched transfer
      # done. Nothing further to check that is worth a login.
      return 0
      ;;
  esac

  [ "$expected" = "$actual" ] \
    || fail "${name} is ${expected} bytes here and '${actual:-nothing}' there. The copy is not good."
}

s3_bucket() { local rest="${TARGET#s3://}"; echo "${rest%%/*}"; }
s3_key() { local rest="${TARGET#s3://}"; local prefix="${rest#*/}"; [ "$prefix" = "$rest" ] && echo "$1" || echo "${prefix%/}/$1"; }

# --- Ship --------------------------------------------------------------------

say "sending ${#DUMPS[@]} dump(s) from '${SOURCE}' to '${TARGET}'"

for dump in "${DUMPS[@]}"; do
  manifest="${dump%.dump}.manifest"
  verify "$dump"

  if [ "$DRY_RUN" = "1" ]; then
    say "would send $(basename "$dump") and its manifest"
    continue
  fi

  case "$TARGET" in
    rsync://*) ship_rsync "$dump" "$manifest" ;;
    s3://*)    ship_s3 "$dump" "$manifest" ;;
    file://*)  ship_file "$dump" "$manifest" ;;
    *)         fail "'${TARGET}' is not a destination this script knows. Use rsync://, s3:// or file:///." ;;
  esac

  confirm_size "$dump"
  say "sent $(basename "$dump")"
done

if [ "$DRY_RUN" = "1" ]; then
  say "dry run: nothing was sent."
fi

say "done"
