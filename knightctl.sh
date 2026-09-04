#!/usr/bin/env bash
#
# knightctl — manage a KNIGHT deployment installed by install.sh.
#
#   knightctl                 an interactive menu
#   knightctl <command>       one thing, for a script or a runbook
#
# Everything it touches belongs to KNIGHT: its own systemd units, its own nginx
# site, its own directory and its own database. Nothing else on the server is
# read or changed, and `uninstall` is held to the same rule.

set -uo pipefail

INSTALL_DIR="/opt/knight"
SRC_DIR="${INSTALL_DIR}/src"
API_DIR="${INSTALL_DIR}/app/api"
BOOTSTRAP_DIR="${INSTALL_DIR}/app/bootstrap"
DASHBOARD_DIR="${INSTALL_DIR}/dashboard"
BACKUP_DIR="${INSTALL_DIR}/backups"
ENV_FILE="${INSTALL_DIR}/knight.env"
SERVICE_USER="knight"

RED=$'\033[0;31m'
GREEN=$'\033[0;32m'
YELLOW=$'\033[1;33m'
CYAN=$'\033[0;36m'
BOLD=$'\033[1m'
DIM=$'\033[2m'
NC=$'\033[0m'

info()    { echo -e "${CYAN}[·]${NC} $1"; }
success() { echo -e "${GREEN}[✓]${NC} $1"; }
warn()    { echo -e "${YELLOW}[!]${NC} $1"; }
error()   { echo -e "${RED}[✗] $1${NC}" >&2; exit 1; }
title()   { echo -e "\n${BOLD}${CYAN}━━━ $1 ━━━${NC}"; }

[[ $EUID -ne 0 ]] && error "knightctl has to run as root:  sudo knightctl $*"
[[ -f "$ENV_FILE" ]] || error "No KNIGHT installation found at ${INSTALL_DIR}."

set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a

DOMAIN="${KNIGHT_DOMAIN:-}"
SCHEME="${KNIGHT_SCHEME:-https}"
APP_PORT="${KNIGHT_APP_PORT:-}"
DOTNET_EXEC="${KNIGHT_DOTNET:-$(command -v dotnet)}"
NODE_BIN="${KNIGHT_NODE_BIN:-}"
DB_CONNECTION="${ConnectionStrings__ControlPlane:-}"

write_env() {
  local key="$1" value="$2"
  local escaped="${value//\'/\'\\'\'}"
  sed -i "/^${key}=/d" "$ENV_FILE"
  printf "%s='%s'\n" "$key" "$escaped" >> "$ENV_FILE"
}

# The checkout is owned by the service user and these commands run as root,
# which git refuses by default: "detected dubious ownership". The exception is
# granted per invocation rather than written into root's global gitconfig, so it
# does not outlive the command and does not apply to any other repository on
# this machine.
git_src() { git -c safe.directory="$SRC_DIR" -C "$SRC_DIR" "$@"; }

# Waits for the host to answer its own readiness probe. Restarting the service
# returns as soon as systemd has started the process, which is several seconds
# before it is serving - long enough for a success message to send somebody to a
# 502 they will reasonably read as a broken deployment.
wait_for_api() {
  local _
  for _ in $(seq 1 45); do
    curl -fsS --max-time 3 "http://127.0.0.1:${APP_PORT}/health/ready" >/dev/null 2>&1 && return 0
    systemctl is-active --quiet knight-api || return 1
    sleep 1
  done
  return 1
}

confirm() {
  local answer
  read -rp "$(echo -e "  ${BOLD}$1 [y/N]${NC}: ")" answer
  [[ "$answer" =~ ^[Yy]$ ]]
}

# --- Commands -----------------------------------------------------------------

cmd_status() {
  title "KNIGHT"

  echo -e "  Domain      ${CYAN}${SCHEME}://${DOMAIN}${NC}"
  echo -e "  API         ${DIM}127.0.0.1:${APP_PORT}${NC}"
  echo -e "  Redis       ${DIM}127.0.0.1:${KNIGHT_REDIS_PORT:-?}${NC}"
  echo -e "  Database    ${DIM}${KNIGHT_DB_NAME:-?} on ${KNIGHT_DB_HOST:-?}:${KNIGHT_DB_PORT:-?}${NC}"
  echo ""

  for unit in knight-api knight-redis nginx postgresql; do
    if systemctl is-active --quiet "$unit"; then
      printf "  %-16s ${GREEN}running${NC}\n" "$unit"
    elif systemctl list-unit-files "${unit}.service" >/dev/null 2>&1 && \
         systemctl cat "$unit" >/dev/null 2>&1; then
      printf "  %-16s ${RED}stopped${NC}\n" "$unit"
    else
      printf "  %-16s ${DIM}not installed${NC}\n" "$unit"
    fi
  done

  if systemctl is-active --quiet knight-backup.timer; then
    printf "  %-16s ${GREEN}scheduled${NC} ${DIM}%s${NC}\n" "nightly backup" \
      "$(systemctl show knight-backup.timer -p NextElapseUSecRealtime --value 2>/dev/null)"
  else
    printf "  %-16s ${YELLOW}not scheduled${NC}\n" "nightly backup"
  fi

  echo ""
  if health="$(curl -fsS --max-time 5 "http://127.0.0.1:${APP_PORT}/health/ready" 2>/dev/null)"; then
    echo -e "  Health      ${GREEN}$(echo "$health" | grep -o '"status":"[^"]*"' | head -1 | cut -d'"' -f4)${NC}"
  else
    echo -e "  Health      ${RED}not answering${NC} ${DIM}(journalctl -u knight-api -n 40)${NC}"
  fi

  if [[ -d "/etc/letsencrypt/live/${DOMAIN}" ]]; then
    expiry="$(openssl x509 -enddate -noout -in "/etc/letsencrypt/live/${DOMAIN}/fullchain.pem" 2>/dev/null | cut -d= -f2)"
    echo -e "  Certificate ${DIM}expires ${expiry}${NC}"
  else
    echo -e "  Certificate ${YELLOW}none — this deployment is on plain HTTP${NC}"
  fi

  local dumps
  dumps="$(find "$BACKUP_DIR" -name '*.dump' 2>/dev/null | wc -l)"
  echo -e "  Backups     ${DIM}${dumps} dump(s) in ${BACKUP_DIR}${NC}"
  echo ""
}

cmd_logs() {
  local unit="${1:-api}"
  case "$unit" in
    api)    journalctl -u knight-api -n 200 -f ;;
    redis)  journalctl -u knight-redis -n 200 -f ;;
    backup) journalctl -u knight-backup -n 200 --no-pager ;;
    nginx)  journalctl -u nginx -n 200 -f ;;
    *)      error "Unknown log: ${unit}. One of: api, redis, backup, nginx." ;;
  esac
}

cmd_start()   { systemctl start knight-redis knight-api && success "Started."; }
cmd_stop()    { systemctl stop knight-api && success "Stopped. Redis and nginx are left running."; }
cmd_restart() {
  systemctl restart knight-api || error "systemd could not restart knight-api."

  if wait_for_api; then
    success "Restarted, and reporting ready."
  else
    warn "Restarted, but the API is not reporting ready. See: knightctl logs api"
  fi
}

# Builds the dashboard and publishes the API and bootstrap tool from whatever is
# checked out in $SRC_DIR right now, then applies migrations. Returns non-zero on
# the first failure instead of exiting, so the caller can decide whether to roll
# back. Leaves knight-api stopped either way — the caller starts it.
deploy_from_src() {
  export PATH="${NODE_BIN}:${PATH}"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

  # Rewritten rather than assumed to have survived the checkout, so a release
  # that needs a different build-time setting gets one.
  cat > "${SRC_DIR}/frontend/knight-dashboard/.env.production" <<'ENVPROD'
VITE_USE_MOCKS=false
VITE_DEFAULT_LOCALE=fa
ENVPROD

  info "Building the dashboard..."
  ( cd "${SRC_DIR}/frontend/knight-dashboard" && npm ci --no-audit --no-fund && npm run build ) 2>&1 | tail -4
  [[ -f "${SRC_DIR}/frontend/knight-dashboard/dist/index.html" ]] \
    || { warn "The dashboard build produced no output."; return 1; }

  info "Publishing the API..."
  systemctl stop knight-api
  "$DOTNET_EXEC" publish "${SRC_DIR}/backend/src/Knight.Api/Knight.Api.csproj" -c Release -o "$API_DIR" --nologo 2>&1 | tail -3
  [[ -f "${API_DIR}/Knight.Api.dll" ]] || { warn "The API publish produced no Knight.Api.dll."; return 1; }
  "$DOTNET_EXEC" publish "${SRC_DIR}/backend/tools/Knight.Bootstrap/Knight.Bootstrap.csproj" -c Release -o "$BOOTSTRAP_DIR" --nologo 2>&1 | tail -3
  [[ -f "${BOOTSTRAP_DIR}/Knight.Bootstrap.dll" ]] || { warn "The bootstrap tool did not build."; return 1; }
  rm -f "${API_DIR}/appsettings.Development.json"

  rm -rf "${DASHBOARD_DIR:?}"/*
  cp -r "${SRC_DIR}/frontend/knight-dashboard/dist/." "$DASHBOARD_DIR/"

  # Checked, not merely shown. A migration that failed under a pipe into tail
  # would hide its exit status, and a schema half-applied is the one thing here
  # that a rollback exists to undo.
  info "Applying migrations..."
  if ! CONTROL_PLANE_DB_CONNECTION_STRING="$DB_CONNECTION" \
       "$DOTNET_EXEC" "${BOOTSTRAP_DIR}/Knight.Bootstrap.dll" --migrate-only; then
    warn "Migrations did not apply."
    return 1
  fi

  chown -R "${SERVICE_USER}:${SERVICE_USER}" "$INSTALL_DIR"
  chmod 755 "$INSTALL_DIR" "$DASHBOARD_DIR"
  find "$DASHBOARD_DIR" -type d -exec chmod 755 {} +
  find "$DASHBOARD_DIR" -type f -exec chmod 644 {} +
  chmod 600 "$ENV_FILE"
  return 0
}

# The core of a restore, without the prompt or the service juggling its callers
# do around it: replaces every row in the control-plane database from a dump.
# Shared by cmd_restore and the update rollback so the two cannot drift.
restore_dump_force() {
  local dump="$1"

  # Dropping and recreating the database is not something the application role
  # can do; where there is a local cluster those statements run as the superuser
  # and everything else as the application role, and where there is not, the
  # restore script falls back and says plainly what it could not do.
  local admin_psql=""
  if runuser -u postgres -- psql -tAc "SELECT 1" >/dev/null 2>&1; then
    admin_psql="runuser -u postgres -- env -u PGHOST -u PGPORT -u PGUSER -u PGPASSWORD psql"
  fi

  PGHOST="${KNIGHT_DB_HOST}" PGPORT="${KNIGHT_DB_PORT}" \
  PGUSER="${KNIGHT_DB_USER}" PGPASSWORD="${KNIGHT_DB_PASSWORD}" \
  KNIGHT_ADMIN_PSQL="$admin_psql" KNIGHT_DB_OWNER="${KNIGHT_DB_USER}" \
    "${SRC_DIR}/infrastructure/scripts/knight-restore.sh" "$dump" "${KNIGHT_DB_NAME}" --force
}

cmd_update() {
  title "Update"

  [[ -d "${SRC_DIR}/.git" ]] || error "${SRC_DIR} is not a git checkout, so there is nothing to update from."

  local before after
  before="$(git_src rev-parse --short HEAD)"

  # The branch this deployment was installed from, not the repository's default
  # one. An update that quietly moved a server onto another branch would be a
  # surprise nobody could see in the output.
  local ref="${KNIGHT_REPO_REF:-main}"

  info "Fetching ${ref}..."
  git_src fetch --depth=1 origin "$ref" -q || error "Could not fetch ${ref}."
  git_src reset --hard FETCH_HEAD -q || error "Could not check the new revision out."
  after="$(git_src rev-parse --short HEAD)"

  if [[ "$before" == "$after" ]]; then
    success "Already at ${after}. Nothing to do."
    return
  fi
  info "${before} → ${after}"

  # Backed up before anything is applied, and this is also the snapshot a failed
  # update is rolled back onto. It is complete precisely because knight-api is
  # stopped from here until the deploy succeeds or is rolled back, so nothing is
  # written past the snapshot for a rollback to lose. The cost is that the API is
  # down for the length of the build rather than only the publish; on a control
  # plane, where stores keep serving from their cached entitlements, a safe
  # rollback is worth the extra minutes of dashboard downtime.
  info "Taking a backup first..."
  systemctl stop knight-api >/dev/null 2>&1
  cmd_backup >/dev/null || { systemctl start knight-api; error "The pre-update backup failed. Nothing has been changed."; }

  local pre_dump
  pre_dump="$(find "$BACKUP_DIR" -name '*.dump' -printf '%T@ %p\n' 2>/dev/null | sort -rn | head -1 | cut -d' ' -f2-)"

  if deploy_from_src && { systemctl start knight-api; wait_for_api; }; then
    success "Updated to ${after} and running."
    return
  fi

  # The new revision did not come up healthy. Put the server back exactly as it
  # was — the previous code and the pre-update database — rather than leave a
  # broken deployment behind. This is the promise 'update' has always made; now
  # it is carried out rather than described.
  warn "The update to ${after} did not come up healthy — rolling back to ${before}."
  systemctl stop knight-api >/dev/null 2>&1

  if ! git_src reset --hard "$before" -q; then
    systemctl start knight-api >/dev/null 2>&1
    error "Could not restore ${before}. Recover by hand; the pre-update dump is ${pre_dump}."
  fi

  if ! deploy_from_src; then
    systemctl start knight-api >/dev/null 2>&1
    error "The previous revision did not rebuild either. The pre-update dump is ${pre_dump}; recover by hand."
  fi

  if [[ -n "$pre_dump" && -f "$pre_dump" ]]; then
    info "Restoring the pre-update database..."
    restore_dump_force "$pre_dump" >/dev/null 2>&1 \
      || warn "The database restore reported a problem; check: knightctl logs api"
  fi

  systemctl start knight-api
  if wait_for_api; then
    warn "Rolled back to ${before}. The update to ${after} was not applied — see why: knightctl logs api"
  else
    error "Rolled back to ${before}, but the API is still not ready. The pre-update dump is ${pre_dump}."
  fi
}

cmd_backup() {
  title "Backup"

  # The same unit the nightly timer runs, rather than the script directly. Two
  # code paths drift, and the first sign of it would be a manual backup writing
  # files the scheduled one cannot rotate - because one ran as root and the
  # other as knight.
  if ! systemctl start knight-backup.service; then
    journalctl -u knight-backup -n 20 --no-pager
    error "The backup failed."
  fi

  journalctl -u knight-backup -n 5 --no-pager --output=cat
  success "Written to ${BACKUP_DIR}"
}

cmd_restore() {
  local dump="${1:-}"

  if [[ -z "$dump" ]]; then
    title "Restore"
    echo -e "  ${DIM}Available dumps:${NC}"
    find "$BACKUP_DIR" -name '*.dump' -printf '  %TY-%Tm-%Td %TH:%TM  %p\n' 2>/dev/null | sort -r | head -20
    echo ""
    read -rp "$(echo -e "  ${BOLD}Dump to restore${NC}: ")" dump
  fi

  [[ -f "$dump" ]] || error "No such dump: ${dump}"

  warn "This replaces every row in ${KNIGHT_DB_NAME}. Customers, stores, credentials, audit — all of it."
  confirm "Restore ${dump} over ${KNIGHT_DB_NAME}?" || { echo "  Nothing was changed."; return; }

  systemctl stop knight-api
  restore_dump_force "$dump"
  local outcome=$?
  systemctl start knight-api

  if [[ $outcome -ne 0 ]]; then
    error "The restore failed. The API has been started again; check what it says about the database."
  fi

  if wait_for_api; then
    success "Restored, and the API is reporting ready."
  else
    warn "Restored, but the API is not reporting ready. See: knightctl logs api"
  fi
}

cmd_admin() {
  title "New administrator"

  local email password confirm_password
  while true; do
    read -rp "$(echo -e "  ${BOLD}Email${NC}: ")" email
    [[ "$email" =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]] && break
    echo -e "  ${RED}That is not an email address.${NC}"
  done

  while true; do
    read -rsp "$(echo -e "  ${BOLD}Password${NC} ${DIM}(10-128 characters)${NC}: ")" password; echo ""
    [[ ${#password} -ge 10 && ${#password} -le 128 ]] || { echo -e "  ${RED}Between 10 and 128 characters.${NC}"; continue; }
    read -rsp "$(echo -e "  ${BOLD}Confirm${NC}: ")" confirm_password; echo ""
    [[ "$password" == "$confirm_password" ]] && break
    echo -e "  ${RED}They do not match.${NC}"
  done

  printf '%s\n%s\n' "$password" "$password" \
    | CONTROL_PLANE_DB_CONNECTION_STRING="$DB_CONNECTION" \
      "$DOTNET_EXEC" "${BOOTSTRAP_DIR}/Knight.Bootstrap.dll" --email "$email"
  unset password confirm_password
}

cmd_domain() {
  local new="${1:-}"
  [[ -z "$new" ]] && read -rp "$(echo -e "  ${BOLD}New domain${NC}: ")" new
  [[ "$new" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]] \
    || error "That is not a hostname."

  title "Domain"
  warn "Point ${new}'s DNS A record at this server before continuing."
  confirm "Move this deployment to ${new}?" || { echo "  Nothing was changed."; return; }

  # Only the hostname changes. The dashboard bundle uses relative URLs, so there
  # is nothing in it to rebuild.
  sed -i "s/^\( *server_name *\).*/\1${new};/" /etc/nginx/sites-available/knight
  nginx -t >/dev/null 2>&1 || error "nginx rejected the change. Run: nginx -t"
  systemctl reload nginx

  local scheme="http"
  if certbot --nginx -d "$new" --non-interactive --agree-tos \
       --email "${KNIGHT_SSL_EMAIL:-admin@${new}}" --redirect >/dev/null 2>&1; then
    scheme="https"
    success "Certificate issued for ${new}"
  else
    warn "No certificate yet. Once DNS points here:  certbot --nginx -d ${new} --redirect"
  fi

  write_env KNIGHT_DOMAIN "$new"
  write_env KNIGHT_SCHEME "$scheme"
  write_env Cors__AllowedOrigins__0 "${scheme}://${new}"
  write_env FeatureArtifacts__PublicBaseUrl "${scheme}://${new}/artifacts"
  [[ -n "${Email__Host:-}" ]] && write_env Email__DashboardBaseUrl "${scheme}://${new}"

  systemctl restart knight-api

  if wait_for_api; then
    success "Now serving ${scheme}://${new}"
  else
    warn "The settings are written, but the API is not reporting ready. See: knightctl logs api"
  fi
}

cmd_signing_key() {
  title "Artifact signing key"
  echo -e "  ${DIM}KNIGHT verifies every Feature package against this key before it is${NC}"
  echo -e "  ${DIM}installed into a customer's store. Only the public half belongs on${NC}"
  echo -e "  ${DIM}this machine; the private half stays wherever custody says it does.${NC}"
  echo ""

  local key_id public_key
  while true; do
    read -rp "$(echo -e "  ${BOLD}Key id${NC} ${DIM}[primary]${NC}: ")" key_id
    key_id="${key_id:-primary}"
    # It becomes part of an environment variable name, so it has to be a valid
    # identifier or this file stops being readable by systemd and by this tool.
    [[ "$key_id" =~ ^[A-Za-z0-9_]+$ ]] && break
    echo -e "  ${RED}Letters, digits and underscores only.${NC}"
  done
  read -rp "$(echo -e "  ${BOLD}Public key, base64 DER${NC}: ")" public_key
  [[ -n "$public_key" ]] || error "No key given. Nothing was changed."

  write_env FeatureArtifacts__ActiveKeyId "$key_id"
  write_env "FeatureArtifacts__Keys__${key_id}__PublicKey" "$public_key"
  systemctl restart knight-api

  if wait_for_api; then
    success "Key '${key_id}' is active. Previously configured keys are kept, so versions they signed still verify."
  else
    warn "The key is written, but the API is not reporting ready. A malformed key is the first thing to check: knightctl logs api"
  fi
}

cmd_config() {
  title "Configuration"
  echo -e "  ${DIM}From ${ENV_FILE}. Secrets are shown as their length only.${NC}"
  echo ""
  while IFS='=' read -r key value; do
    [[ -z "$key" || "$key" == \#* ]] && continue
    value="${value%\'}"; value="${value#\'}"
    case "$key" in
      *Password*|*PASSWORD*|*SigningKey*|*Secret*|ConnectionStrings__*)
        printf "  %-46s ${DIM}(%d characters, hidden)${NC}\n" "$key" "${#value}" ;;
      *)
        printf "  %-46s %s\n" "$key" "$value" ;;
    esac
  done < "$ENV_FILE"
  echo ""
}

cmd_doctor() {
  title "Checks"
  local problems=0

  check() {                     # check <description> <command...>
    local description="$1"; shift
    if "$@" >/dev/null 2>&1; then
      printf "  ${GREEN}OK${NC}   %s\n" "$description"
    else
      printf "  ${RED}FAIL${NC} %s\n" "$description"
      problems=$((problems + 1))
    fi
  }

  check "knight-api is running"           systemctl is-active --quiet knight-api
  check "knight-redis is running"         systemctl is-active --quiet knight-redis
  check "nginx is running"                systemctl is-active --quiet nginx
  check "nginx configuration is valid"    nginx -t
  check "the API reports ready"           curl -fsS --max-time 5 "http://127.0.0.1:${APP_PORT}/health/ready"
  check "the nightly backup is scheduled" systemctl is-active --quiet knight-backup.timer
  check "the site answers"                curl -fsS --max-time 10 -o /dev/null "${SCHEME}://${DOMAIN}/"

  check "the database accepts a login" \
    env PGPASSWORD="${KNIGHT_DB_PASSWORD}" psql \
      -h "${KNIGHT_DB_HOST}" -p "${KNIGHT_DB_PORT}" -U "${KNIGHT_DB_USER}" \
      -d "${KNIGHT_DB_NAME}" -tAc "SELECT 1"

  check "Redis answers" \
    redis-cli -h 127.0.0.1 -p "${KNIGHT_REDIS_PORT}" -a "${KNIGHT_REDIS_PASSWORD}" --no-auth-warning ping

  # A dump older than two days means the timer has been failing quietly, which
  # is the state a backup is least likely to be noticed in.
  if find "$BACKUP_DIR" -name "*.dump" -mtime -2 2>/dev/null | grep -q .; then
    printf "  ${GREEN}OK${NC}   %s\n" "a backup was taken in the last two days"
  else
    printf "  ${YELLOW}WARN${NC} %s\n" "no backup in the last two days - check: knightctl logs backup"
    problems=$((problems + 1))
  fi

  if [[ -z "${FeatureArtifacts__ActiveKeyId:-}" ]]; then
    printf "  ${YELLOW}WARN${NC} %s\n" "no artifact signing key - Feature versions cannot be published"
  fi

  local used
  used="$(df --output=pcent "$INSTALL_DIR" 2>/dev/null | tail -1 | tr -dc "0-9")"
  if [[ -n "$used" && "$used" -gt 90 ]]; then
    printf "  ${RED}FAIL${NC} %s\n" "the disk holding ${INSTALL_DIR} is ${used}% full"
    problems=$((problems + 1))
  else
    printf "  ${GREEN}OK${NC}   %s\n" "disk space (${used:-?}% used)"
  fi

  echo ""
  if [[ $problems -eq 0 ]]; then
    success "Nothing wrong found."
  else
    warn "${problems} problem(s). See above."
  fi
}

cmd_uninstall() {
  title "Uninstall"
  echo -e "  ${DIM}This removes the services, the nginx site and the files that belong${NC}"
  echo -e "  ${DIM}to KNIGHT. Everything else on this server is left exactly as it is:${NC}"
  echo -e "  ${DIM}other nginx sites, other databases, and the system-wide .NET, Node,${NC}"
  echo -e "  ${DIM}PostgreSQL and Redis packages that other applications may be using.${NC}"
  echo ""

  confirm "Uninstall KNIGHT?" || { echo "  Nothing was changed."; return; }

  local drop_database=false delete_files=false remove_user=false
  confirm "Also drop the ${KNIGHT_DB_NAME} database? Every customer, store and audit row goes with it" \
    && drop_database=true
  confirm "Also delete ${INSTALL_DIR}, including ${BACKUP_DIR}?" && delete_files=true
  if $delete_files && confirm "Also remove the ${SERVICE_USER} system user?"; then
    remove_user=true
  fi

  info "Stopping services..."
  systemctl disable --now knight-api knight-redis knight-backup.timer >/dev/null 2>&1
  rm -f /etc/systemd/system/knight-api.service \
        /etc/systemd/system/knight-redis.service \
        /etc/systemd/system/knight-backup.service \
        /etc/systemd/system/knight-backup.timer
  systemctl daemon-reload
  success "Services removed"

  rm -f /etc/nginx/sites-enabled/knight \
        /etc/nginx/sites-available/knight \
        /etc/nginx/conf.d/knight-shared.conf
  if nginx -t >/dev/null 2>&1; then
    systemctl reload nginx >/dev/null 2>&1
    success "nginx site removed, and every other site is still served"
  else
    warn "nginx reports a problem now that the site is gone. Check: nginx -t"
  fi

  if $drop_database; then
    runuser -u postgres -- dropdb --if-exists "${KNIGHT_DB_NAME}" >/dev/null 2>&1
    runuser -u postgres -- psql -qc "DROP ROLE IF EXISTS \"${KNIGHT_DB_USER}\";" >/dev/null 2>&1
    success "Database and role dropped"
  else
    info "The ${KNIGHT_DB_NAME} database was left in place."
  fi

  if $delete_files; then
    rm -rf "$INSTALL_DIR"
    success "${INSTALL_DIR} deleted"
    if $remove_user; then
      userdel "$SERVICE_USER" >/dev/null 2>&1 && success "The ${SERVICE_USER} user was removed"
    fi
  else
    info "${INSTALL_DIR} was left in place, backups and all."
  fi

  # Deliberately not revoked. Reinstalling on the same hostname reuses it, and
  # revoking a certificate another vhost happens to share would break that vhost.
  info "The certificate for ${DOMAIN} was left alone. Remove it with: certbot delete --cert-name ${DOMAIN}"

  rm -f /usr/local/bin/knightctl
  echo ""
  success "KNIGHT has been uninstalled."
}

# --- Menu ---------------------------------------------------------------------

menu() {
  while true; do
    echo ""
    echo -e "${BOLD}${CYAN}  KNIGHT${NC}  ${DIM}${SCHEME}://${DOMAIN}${NC}"
    echo ""
    echo -e "   ${BOLD}1${NC}  Status              ${DIM}what is running, and whether it is healthy${NC}"
    echo -e "   ${BOLD}2${NC}  Checks              ${DIM}run every check and report what is wrong${NC}"
    echo -e "   ${BOLD}3${NC}  Logs                ${DIM}follow the API log${NC}"
    echo -e "   ${BOLD}4${NC}  Restart"
    echo -e "   ${BOLD}5${NC}  Update              ${DIM}pull, rebuild, migrate, restart${NC}"
    echo -e "   ${BOLD}6${NC}  Back up now"
    echo -e "   ${BOLD}7${NC}  Restore a backup"
    echo -e "   ${BOLD}8${NC}  Add an administrator"
    echo -e "   ${BOLD}9${NC}  Change the domain"
    echo -e "  ${BOLD}10${NC}  Set the artifact signing key"
    echo -e "  ${BOLD}11${NC}  Show configuration"
    echo -e "  ${BOLD}12${NC}  Uninstall"
    echo -e "   ${BOLD}q${NC}  Quit"
    echo ""
    read -rp "$(echo -e "  ${BOLD}Choice${NC}: ")" choice

    case "$choice" in
      1)   cmd_status ;;
      2)   cmd_doctor ;;
      3)   cmd_logs api ;;
      4)   cmd_restart ;;
      5)   cmd_update ;;
      6)   cmd_backup ;;
      7)   cmd_restore ;;
      8)   cmd_admin ;;
      9)   cmd_domain ;;
      10)  cmd_signing_key ;;
      11)  cmd_config ;;
      12)  cmd_uninstall; exit 0 ;;
      q|Q) exit 0 ;;
      *)   warn "No such choice." ;;
    esac
  done
}

usage() {
  cat <<USAGE
knightctl - manage the KNIGHT deployment at ${INSTALL_DIR}

  knightctl                     interactive menu
  knightctl status              what is running, and whether it is healthy
  knightctl doctor              run every check and report what is wrong
  knightctl logs [api|redis|backup|nginx]
  knightctl start|stop|restart
  knightctl update              pull, rebuild, migrate and restart — rolls back on failure
  knightctl backup              take a dump now
  knightctl restore [dump]      restore one over the control-plane database
  knightctl admin               create an administrator
  knightctl domain <hostname>   move this deployment to another hostname
  knightctl signing-key         set the artifact signing public key
  knightctl config              show the configuration, secrets elided
  knightctl uninstall           remove KNIGHT and nothing else
USAGE
}

case "${1:-}" in
  ""|menu)        menu ;;
  status)         cmd_status ;;
  doctor)         cmd_doctor ;;
  logs)           shift; cmd_logs "$@" ;;
  start)          cmd_start ;;
  stop)           cmd_stop ;;
  restart)        cmd_restart ;;
  update)         cmd_update ;;
  backup)         cmd_backup ;;
  restore)        shift; cmd_restore "$@" ;;
  admin)          cmd_admin ;;
  domain)         shift; cmd_domain "$@" ;;
  signing-key)    cmd_signing_key ;;
  config)         cmd_config ;;
  uninstall)      cmd_uninstall ;;
  -h|--help|help) usage ;;
  *)              usage; exit 1 ;;
esac
