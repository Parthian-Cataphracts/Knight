#!/usr/bin/env bash
#
# KNIGHT — one-command installer for Ubuntu 22.04+ and Debian 12+.
#
#   bash <(curl -Ls https://raw.githubusercontent.com/Parthian-Cataphracts/Knight/main/install.sh)
#
# Everything KNIGHT owns lives under /opt/knight and runs as the unprivileged
# `knight` system user. The script is written for a server that already hosts
# other applications, which is the normal case and the reason for most of the
# decisions in it:
#
#   * It never replaces a system-wide .NET or Node that another application
#     depends on. Where the host's version is too old it installs a private copy
#     under /opt/knight/toolchain and builds with that instead.
#   * It picks free TCP ports rather than assuming 5000 or 6379, and everything
#     except nginx listens on 127.0.0.1 only.
#   * It runs its own Redis instance with its own password and a memory ceiling,
#     so no other application's keyspace is shared, evicted or flushed, and a
#     runaway KNIGHT cannot starve the rest of the box.
#   * It creates one PostgreSQL role and one database and touches no others.
#   * It adds one nginx site and one uniquely-named file in conf.d. It does not
#     edit nginx.conf, and it does not disable anybody else's site.
#
# Re-running it is safe. Existing secrets, artifacts and backups are kept, and
# nothing is asked twice.

# Fail on an unset variable, and let a failing stage in a pipeline fail the
# pipeline. Deliberately not `set -e`: this script checks the results it cares
# about and reports them in a sentence, which is more use to whoever is watching
# than an unexplained exit part-way through an install.
set -uo pipefail

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
step()    { echo -e "\n${BOLD}${CYAN}━━━ $1 ━━━${NC}"; }

# --- Preflight ----------------------------------------------------------------

[[ $EUID -ne 0 ]] && error "Run this as root:  sudo bash install.sh"

command -v apt-get >/dev/null || error \
  "This installer supports Debian and Ubuntu. On another distribution, follow docs/installation.md instead."

# `curl | bash` leaves stdin attached to the pipe, so every prompt below would
# read end-of-file and the install would proceed on blank answers. The process
# substitution form keeps the terminal on stdin, which is why it is the
# documented one.
ASSUME_YES="${KNIGHT_ASSUME_YES:-0}"
if [[ ! -t 0 && "$ASSUME_YES" != "1" ]]; then
  error "$(cat <<'EOF'
This installer asks questions, and stdin is not a terminal.

Use:   bash <(curl -Ls https://raw.githubusercontent.com/Parthian-Cataphracts/Knight/main/install.sh)
Not:   curl -Ls ... | bash

To run it unattended instead, set KNIGHT_ASSUME_YES=1 together with
KNIGHT_DOMAIN, KNIGHT_SSL_EMAIL, KNIGHT_ADMIN_EMAIL and KNIGHT_ADMIN_PASSWORD.
EOF
)"
fi

# --- Layout -------------------------------------------------------------------

INSTALL_DIR="/opt/knight"
SRC_DIR="${INSTALL_DIR}/src"
APP_DIR="${INSTALL_DIR}/app"
API_DIR="${APP_DIR}/api"
BOOTSTRAP_DIR="${APP_DIR}/bootstrap"
DASHBOARD_DIR="${INSTALL_DIR}/dashboard"
ARTIFACT_DIR="${INSTALL_DIR}/artifacts"
BACKUP_DIR="${INSTALL_DIR}/backups"
STORAGE_DIR="${INSTALL_DIR}/storage"
STATE_DIR="${INSTALL_DIR}/state"
REDIS_DIR="${INSTALL_DIR}/redis"
TOOLCHAIN_DIR="${INSTALL_DIR}/toolchain"
ENV_FILE="${INSTALL_DIR}/knight.env"
BUILD_TMP="${INSTALL_DIR}/.build"

SERVICE_USER="knight"
DEFAULT_REPO_URL="https://github.com/Parthian-Cataphracts/Knight.git"

DOTNET_MAJOR=10
NODE_MAJOR=20
NODE_VERSION="v22.14.0"     # only used when the host has nothing new enough

# A secret with an alphabet of exactly [A-Za-z0-9] so it can be written into a
# systemd EnvironmentFile, a PostgreSQL password literal and a Redis config
# directive without any of the three needing to think about quoting.
random_secret() { head -c 256 /dev/urandom | tr -dc 'A-Za-z0-9' | head -c "${1:-48}"; }

port_in_use() { ss -ltnH 2>/dev/null | awk '{print $4}' | grep -qE "[:.]${1}\$"; }
find_free_port() { local p="${1:-5080}"; while port_in_use "$p"; do p=$((p + 1)); done; echo "$p"; }

# Writes KEY='value' into the environment file, single-quoted and with any
# embedded quote escaped. systemd and bash read that identically, which matters
# because this file is both a systemd EnvironmentFile and something knightctl
# sources.
write_env() {
  local key="$1" value="$2"
  local escaped="${value//\'/\'\\'\'}"
  sed -i "/^${key}=/d" "$ENV_FILE" 2>/dev/null
  printf "%s='%s'\n" "$key" "$escaped" >> "$ENV_FILE"
}

# --- Banner -------------------------------------------------------------------

clear 2>/dev/null
C1=$'\033[38;5;33m'
C2=$'\033[38;5;39m'
CW=$'\033[1;37m'

echo ""
echo -e "${C1}  ██╗  ██╗███╗   ██╗██╗ ██████╗ ██╗  ██╗████████╗"
echo -e "${C1}  ██║ ██╔╝████╗  ██║██║██╔════╝ ██║  ██║╚══██╔══╝"
echo -e "${C2}  █████╔╝ ██╔██╗ ██║██║██║  ███╗███████║   ██║   "
echo -e "${C2}  ██╔═██╗ ██║╚██╗██║██║██║   ██║██╔══██║   ██║   "
echo -e "${C1}  ██║  ██╗██║ ╚████║██║╚██████╔╝██║  ██║   ██║   "
echo -e "${C1}  ╚═╝  ╚═╝╚═╝  ╚═══╝╚═╝ ╚═════╝ ╚═╝  ╚═╝   ╚═╝   ${NC}"
echo ""
echo -e "  ${CW}Control plane for independent Django stores${NC}"
echo ""
echo -e "  ${DIM}┌──────────────────────────────────────────────────┐${NC}"
echo -e "  ${DIM}│${NC}  Stack     ASP.NET Core 10 · React · PostgreSQL   ${DIM}│${NC}"
echo -e "  ${DIM}│${NC}  Installs  ${INSTALL_DIR} (and nowhere else)        ${DIM}│${NC}"
echo -e "  ${DIM}│${NC}  Runs as   ${SERVICE_USER} · 127.0.0.1 only              ${DIM}│${NC}"
echo -e "  ${DIM}│${NC}  Platform  Ubuntu 22.04+ · Debian 12+             ${DIM}│${NC}"
echo -e "  ${DIM}└──────────────────────────────────────────────────┘${NC}"
echo ""

REINSTALL=false
if [[ -f "$ENV_FILE" ]]; then
  REINSTALL=true
  warn "An existing installation was found. Secrets, artifacts and backups will be kept."
  echo ""
fi

# Anything the operator named on this run is captured before the stored answers
# are read, so an explicit KNIGHT_DOMAIN on a re-install wins over the domain
# the last one recorded rather than being silently ignored.
ARG_DOMAIN="${KNIGHT_DOMAIN:-}"
ARG_SSL_EMAIL="${KNIGHT_SSL_EMAIL:-}"
ARG_DB_NAME="${KNIGHT_DB_NAME:-}"
ARG_DB_USER="${KNIGHT_DB_USER:-}"
ARG_REPO_URL="${KNIGHT_REPO_URL:-}"
ARG_REPO_REF="${KNIGHT_REPO_REF:-}"

# Carry a previous install's answers forward so a re-run neither asks twice nor
# invents a second set of secrets. Values are single-quoted by write_env, so
# sourcing is safe.
STORED_DB_USER=""
if $REINSTALL; then
  set -a
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  set +a
  STORED_DB_USER="${KNIGHT_DB_USER:-}"
fi

PREV_DOMAIN="${ARG_DOMAIN:-${KNIGHT_DOMAIN:-}}"
PREV_SSL_EMAIL="${ARG_SSL_EMAIL:-${KNIGHT_SSL_EMAIL:-}}"
PREV_APP_PORT="${KNIGHT_APP_PORT:-}"
PREV_REDIS_PORT="${KNIGHT_REDIS_PORT:-}"
PREV_DB_NAME="${ARG_DB_NAME:-${KNIGHT_DB_NAME:-}}"
PREV_DB_USER="${ARG_DB_USER:-${KNIGHT_DB_USER:-}}"
PREV_DB_PASSWORD="${KNIGHT_DB_PASSWORD:-}"

# Where this deployment tracks. Recorded, so that a re-install and knightctl
# update follow the branch it was installed from rather than assuming the
# repository's default one.
REPO_URL="${ARG_REPO_URL:-${KNIGHT_REPO_URL:-$DEFAULT_REPO_URL}}"
REPO_REF="${ARG_REPO_REF:-${KNIGHT_REPO_REF:-main}}"

# ══════════════════════════════════════════════════════════════════════════════
#  Questions — all of them, before anything is installed
# ══════════════════════════════════════════════════════════════════════════════

step "Configuration"
echo ""
echo -e "  ${DIM}Everything is asked now, so the rest of the install can run unattended.${NC}"
echo ""

ask() {                       # ask <variable> <prompt> <default>
  local __var="$1" __prompt="$2" __default="${3:-}" __answer=""
  if [[ "$ASSUME_YES" == "1" ]]; then
    printf -v "$__var" '%s' "$__default"
    return
  fi
  if [[ -n "$__default" ]]; then
    read -rp "$(echo -e "  ${BOLD}${__prompt}${NC} ${DIM}[${__default}]${NC}: ")" __answer
  else
    read -rp "$(echo -e "  ${BOLD}${__prompt}${NC}: ")" __answer
  fi
  printf -v "$__var" '%s' "${__answer:-$__default}"
}

# --- Domain -------------------------------------------------------------------

echo -e "  ${YELLOW}Point the domain's DNS A record at this server before continuing,${NC}"
echo -e "  ${YELLOW}or the certificate cannot be issued.${NC}"
echo ""
echo -e "  ${DIM}The dashboard, the API and the agent endpoints all share one${NC}"
echo -e "  ${DIM}hostname: one DNS record, one certificate, and no cross-origin${NC}"
echo -e "  ${DIM}requests to get wrong.${NC}"
echo ""

while true; do
  ask DOMAIN "Domain" "$PREV_DOMAIN"
  DOMAIN="$(echo "$DOMAIN" | tr '[:upper:]' '[:lower:]' | tr -d ' ')"
  if [[ "$DOMAIN" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]]; then
    break
  fi
  echo -e "  ${RED}That is not a hostname. Example: knight.example.com${NC}"
done

ask SSL_EMAIL "Email for certificate expiry notices" "${PREV_SSL_EMAIL:-admin@${DOMAIN}}"

# --- First administrator ------------------------------------------------------

echo ""
echo -e "  ${DIM}The first administrator holds SuperAdmin, which requires a second${NC}"
echo -e "  ${DIM}factor. Its first sign-in will ask you to enrol an authenticator${NC}"
echo -e "  ${DIM}app before it can reach anything else.${NC}"
echo ""

ADMIN_EMAIL=""
ADMIN_PASSWORD=""
if ! $REINSTALL; then
  while true; do
    ask ADMIN_EMAIL "Administrator email" "${KNIGHT_ADMIN_EMAIL:-}"
    [[ "$ADMIN_EMAIL" =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]] && break
    echo -e "  ${RED}That is not an email address.${NC}"
  done

  if [[ "$ASSUME_YES" == "1" ]]; then
    ADMIN_PASSWORD="${KNIGHT_ADMIN_PASSWORD:-}"
    [[ ${#ADMIN_PASSWORD} -ge 10 ]] || error "KNIGHT_ADMIN_PASSWORD must be at least 10 characters."
  else
    while true; do
      read -rsp "$(echo -e "  ${BOLD}Administrator password${NC} ${DIM}(10-128 characters)${NC}: ")" ADMIN_PASSWORD; echo ""
      if [[ ${#ADMIN_PASSWORD} -lt 10 || ${#ADMIN_PASSWORD} -gt 128 ]]; then
        echo -e "  ${RED}Between 10 and 128 characters.${NC}"; continue
      fi
      read -rsp "$(echo -e "  ${BOLD}Confirm${NC}: ")" ADMIN_CONFIRM; echo ""
      [[ "$ADMIN_PASSWORD" == "$ADMIN_CONFIRM" ]] && break
      echo -e "  ${RED}They do not match.${NC}"
    done
  fi
else
  info "Administrator accounts already exist; not creating another. Use 'knightctl admin' to add one."
fi

# --- Database -----------------------------------------------------------------

echo ""
echo -e "  ${DIM}KNIGHT needs one PostgreSQL database of its own. A local server is${NC}"
echo -e "  ${DIM}used if there is one, and installed if there is not. Nothing already${NC}"
echo -e "  ${DIM}on it is touched.${NC}"
echo ""

DB_NAME="${PREV_DB_NAME:-knight}"
DB_USER="${PREV_DB_USER:-knight}"
if ! $REINSTALL; then
  ask DB_NAME "Database name" "${PREV_DB_NAME:-knight}"
  ask DB_USER "Database role" "${PREV_DB_USER:-knight}"
fi

# --- Artifact signing ---------------------------------------------------------

echo ""
echo -e "  ${DIM}KNIGHT installs only signed Feature packages, and verifies every one${NC}"
echo -e "  ${DIM}against a public key. The private half is generated and kept wherever${NC}"
echo -e "  ${DIM}custody says it lives — never on this machine, and never by this${NC}"
echo -e "  ${DIM}script (docs/risks.md R21).${NC}"
echo ""
echo -e "  ${DIM}Skipping is fine: KNIGHT installs and runs, and publishing a Feature${NC}"
echo -e "  ${DIM}version stays unavailable until a key is set with 'knightctl signing-key'.${NC}"
echo ""

# Whatever a previous run configured. Keys are stored under their own id, so
# this is the one name that has to be looked up rather than assumed.
STORED_ACTIVE_KEY_ID="${FeatureArtifacts__ActiveKeyId:-}"

SIGNING_KEY_ID="${KNIGHT_SIGNING_KEY_ID:-}"
SIGNING_PUBLIC_KEY="${KNIGHT_SIGNING_PUBLIC_KEY:-}"

if [[ -n "$STORED_ACTIVE_KEY_ID" && -z "$SIGNING_PUBLIC_KEY" ]]; then
  info "Signing key '${STORED_ACTIVE_KEY_ID}' is already configured and is kept. Change it with 'knightctl signing-key'."
else
  ask SIGNING_PUBLIC_KEY "Artifact signing public key, base64 DER (Enter to skip)" "$SIGNING_PUBLIC_KEY"
  if [[ -n "$SIGNING_PUBLIC_KEY" ]]; then
    # The id becomes part of an environment variable name
    # (FeatureArtifacts__Keys__<id>__PublicKey), so anything that is not a valid
    # identifier would produce a configuration file that neither systemd nor
    # knightctl can read - and the failure would come later, somewhere else.
    while true; do
      ask SIGNING_KEY_ID "Key id (letters, digits and underscores)" "${SIGNING_KEY_ID:-primary}"
      [[ "$SIGNING_KEY_ID" =~ ^[A-Za-z0-9_]+$ ]] && break
      echo -e "  ${RED}Letters, digits and underscores only.${NC}"
      [[ "$ASSUME_YES" == "1" ]] && error "KNIGHT_SIGNING_KEY_ID must be letters, digits and underscores only."
    done
  fi
fi

# --- Outbound mail ------------------------------------------------------------

echo ""
echo -e "  ${DIM}Optional. With a mail server, a new administrator gets an activation${NC}"
echo -e "  ${DIM}link and sets their own password. Without one, KNIGHT falls back to a${NC}"
echo -e "  ${DIM}one-time password shown in the dashboard and says which happened.${NC}"
echo ""

SMTP_HOST="${KNIGHT_SMTP_HOST:-}"
SMTP_PORT="${KNIGHT_SMTP_PORT:-587}"
SMTP_USER="${KNIGHT_SMTP_USER:-}"
SMTP_PASSWORD="${KNIGHT_SMTP_PASSWORD:-}"
SMTP_FROM="${KNIGHT_SMTP_FROM:-}"
if ! $REINSTALL; then
  ask SMTP_HOST "SMTP host (Enter to skip)" ""
  if [[ -n "$SMTP_HOST" ]]; then
    ask SMTP_PORT "SMTP port" "587"
    ask SMTP_FROM "From address" "knight@${DOMAIN}"
    ask SMTP_USER "SMTP username (Enter for none)" ""
    if [[ -n "$SMTP_USER" && "$ASSUME_YES" != "1" ]]; then
      read -rsp "$(echo -e "  ${BOLD}SMTP password${NC}: ")" SMTP_PASSWORD; echo ""
    fi
  fi
fi

# --- Confirm ------------------------------------------------------------------

echo ""
echo -e "  ${GREEN}${BOLD}Summary${NC}"
echo -e "  Domain          ${CYAN}${DOMAIN}${NC} ${DIM}(https once the certificate is issued)${NC}"
echo -e "  Install root    ${CYAN}${INSTALL_DIR}${NC}"
echo -e "  Runs as         ${CYAN}${SERVICE_USER}${NC}"
echo -e "  Database        ${CYAN}${DB_NAME}${NC} owned by ${CYAN}${DB_USER}${NC}"
echo -e "  Redis           ${CYAN}a dedicated instance, 127.0.0.1${NC}"
echo -e "  Signing key     ${CYAN}$([[ -n "$SIGNING_PUBLIC_KEY" ]] && echo "configured" || echo "not set — Feature publishing disabled")${NC}"
echo -e "  Mail            ${CYAN}$([[ -n "$SMTP_HOST" ]] && echo "$SMTP_HOST:$SMTP_PORT" || echo "none — one-time passwords instead")${NC}"
echo ""

if [[ "$ASSUME_YES" != "1" ]]; then
  read -rp "$(echo -e "  ${BOLD}Proceed? [y/N]${NC}: ")" CONFIRM
  [[ "$CONFIRM" =~ ^[Yy]$ ]] || { echo "  Nothing was changed."; exit 0; }
fi

# ══════════════════════════════════════════════════════════════════════════════
#  System packages
# ══════════════════════════════════════════════════════════════════════════════

step "System packages"

export DEBIAN_FRONTEND=noninteractive

info "Updating package lists..."
apt-get update -qq || error "apt-get update failed. Fix the package sources and re-run."

info "Installing base tools..."
apt-get install -y -qq \
  curl wget git unzip xz-utils ca-certificates gnupg lsb-release \
  iproute2 nginx certbot python3-certbot-nginx openssl libicu-dev \
  || error "Could not install the base packages."
success "curl, git, nginx, certbot installed"

# Only ever disabled if this script is what put it there — a Redis another
# application was already using is left exactly as it was found.
REDIS_WAS_PRESENT=true
command -v redis-server >/dev/null || REDIS_WAS_PRESENT=false

if ! $REDIS_WAS_PRESENT; then
  info "Installing redis-server..."
  apt-get install -y -qq redis-server || error "Could not install redis-server."
  # KNIGHT runs its own instance below. The stock one on 6379 is this script's
  # own doing and nothing uses it, so it is stopped rather than left listening.
  systemctl disable --now redis-server >/dev/null 2>&1
  success "redis-server installed (the stock instance is stopped; KNIGHT runs its own)"
else
  success "redis-server already present — it is left untouched"
fi

# --- Service user -------------------------------------------------------------

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --home-dir "$INSTALL_DIR" --shell /usr/sbin/nologin "$SERVICE_USER" \
    || error "Could not create the ${SERVICE_USER} system user."
  success "Created the ${SERVICE_USER} system user"
else
  success "The ${SERVICE_USER} system user already exists"
fi

mkdir -p "$SRC_DIR" "$API_DIR" "$BOOTSTRAP_DIR" "$DASHBOARD_DIR" "$ARTIFACT_DIR" \
         "$BACKUP_DIR" "$STORAGE_DIR" "$STATE_DIR" "$REDIS_DIR" "$TOOLCHAIN_DIR" "$BUILD_TMP"
touch "$ENV_FILE"
chmod 600 "$ENV_FILE"

# ══════════════════════════════════════════════════════════════════════════════
#  Build toolchain — private wherever the host's is too old
# ══════════════════════════════════════════════════════════════════════════════

step "Build toolchain"

case "$(uname -m)" in
  x86_64)          ARCH_NODE="x64";   ARCH_DOTNET="x64"   ;;
  aarch64|arm64)   ARCH_NODE="arm64"; ARCH_DOTNET="arm64" ;;
  *) error "Unsupported architecture: $(uname -m). KNIGHT is built for x86_64 and arm64." ;;
esac

# --- .NET ---------------------------------------------------------------------

PRIVATE_DOTNET="${TOOLCHAIN_DIR}/dotnet/dotnet"
DOTNET_EXEC=""

host_dotnet_ok() {
  command -v dotnet >/dev/null || return 1
  dotnet --list-sdks 2>/dev/null | grep -qE "^${DOTNET_MAJOR}\."
}

if [[ -x "$PRIVATE_DOTNET" ]] && "$PRIVATE_DOTNET" --list-sdks 2>/dev/null | grep -qE "^${DOTNET_MAJOR}\."; then
  DOTNET_EXEC="$PRIVATE_DOTNET"
  success ".NET ${DOTNET_MAJOR} already installed under ${TOOLCHAIN_DIR}"
elif host_dotnet_ok; then
  DOTNET_EXEC="$(command -v dotnet)"
  success "Using the host's .NET SDK: $("$DOTNET_EXEC" --version)"
else
  # Installed under ${TOOLCHAIN_DIR} on purpose, and not symlinked onto PATH.
  # Putting a second .NET into /usr/share/dotnet is how an unrelated
  # application on this server discovers its runtime has moved underneath it.
  info "No .NET ${DOTNET_MAJOR} SDK on this host — installing a private copy under ${TOOLCHAIN_DIR}..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${BUILD_TMP}/dotnet-install.sh" \
    || error "Could not download dotnet-install.sh."
  chmod +x "${BUILD_TMP}/dotnet-install.sh"
  "${BUILD_TMP}/dotnet-install.sh" --channel "${DOTNET_MAJOR}.0" --architecture "$ARCH_DOTNET" \
    --install-dir "${TOOLCHAIN_DIR}/dotnet" 2>&1 | tail -3
  [[ -x "$PRIVATE_DOTNET" ]] || error ".NET ${DOTNET_MAJOR} installation failed."
  DOTNET_EXEC="$PRIVATE_DOTNET"
  success ".NET installed privately: $("$DOTNET_EXEC" --version)"
fi

# --- Node ---------------------------------------------------------------------

PRIVATE_NODE_DIR="${TOOLCHAIN_DIR}/node"
NODE_BIN=""

host_node_ok() {
  command -v node >/dev/null || return 1
  [[ "$(node -v 2>/dev/null | sed 's/^v//; s/\..*//')" -ge $NODE_MAJOR ]]
}

if [[ -x "${PRIVATE_NODE_DIR}/bin/node" ]]; then
  NODE_BIN="${PRIVATE_NODE_DIR}/bin"
  success "Node already installed under ${TOOLCHAIN_DIR}: $("${NODE_BIN}/node" -v)"
elif host_node_ok; then
  NODE_BIN="$(dirname "$(command -v node)")"
  success "Using the host's Node: $(node -v)"
else
  # Same reasoning as .NET, and more pressing: `apt install nodejs` would
  # replace whatever Node another application on this server is running on.
  info "No Node ${NODE_MAJOR}+ on this host — installing a private copy under ${TOOLCHAIN_DIR}..."
  tarball="node-${NODE_VERSION}-linux-${ARCH_NODE}.tar.xz"
  curl -fsSL "https://nodejs.org/dist/${NODE_VERSION}/${tarball}" -o "${BUILD_TMP}/${tarball}" \
    || error "Could not download Node ${NODE_VERSION}."
  rm -rf "$PRIVATE_NODE_DIR"; mkdir -p "$PRIVATE_NODE_DIR"
  tar -xJf "${BUILD_TMP}/${tarball}" -C "$PRIVATE_NODE_DIR" --strip-components=1 \
    || error "Could not unpack Node."
  rm -f "${BUILD_TMP}/${tarball}"
  NODE_BIN="${PRIVATE_NODE_DIR}/bin"
  success "Node installed privately: $("${NODE_BIN}/node" -v)"
fi

# --- Ports --------------------------------------------------------------------

APP_PORT="${PREV_APP_PORT:-}"
[[ -z "$APP_PORT" ]] && APP_PORT="$(find_free_port 5080)"
port_in_use "$APP_PORT" && ! $REINSTALL && APP_PORT="$(find_free_port "$APP_PORT")"

REDIS_PORT="${PREV_REDIS_PORT:-}"
[[ -z "$REDIS_PORT" ]] && REDIS_PORT="$(find_free_port 6380)"

info "API will listen on 127.0.0.1:${APP_PORT}, Redis on 127.0.0.1:${REDIS_PORT}"

# ══════════════════════════════════════════════════════════════════════════════
#  Source
# ══════════════════════════════════════════════════════════════════════════════

step "Source"

# The checkout is owned by the service user and these commands run as root,
# which git refuses by default: "detected dubious ownership". The exception is
# granted per invocation rather than written into root's global gitconfig, so it
# does not outlive the command and does not apply to any other repository on
# this machine.
git_src() { git -c safe.directory="$SRC_DIR" -C "$SRC_DIR" "$@"; }

if [[ -d "${SRC_DIR}/.git" ]]; then
  info "Updating the existing checkout..."
  git_src remote set-url origin "$REPO_URL"
  git_src fetch --depth=1 origin "$REPO_REF" -q || error "Could not fetch ${REPO_REF} from ${REPO_URL}."

  # FETCH_HEAD rather than origin/<ref>: what was just fetched is what should be
  # checked out, and a shallow single-branch clone does not always have a
  # remote-tracking ref by that name to reach for.
  git_src reset --hard FETCH_HEAD -q || error "Could not check out ${REPO_REF}."
else
  # A checkout beside the script wins, so the installer can be run from a clone
  # to deploy exactly what is in it.
  SCRIPT_DIR=""
  [[ -n "${BASH_SOURCE[0]:-}" ]] && SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd || true)"

  if [[ -n "$SCRIPT_DIR" && -f "${SCRIPT_DIR}/backend/Knight.slnx" ]]; then
    info "Using the checkout this script was run from..."
    rm -rf "$SRC_DIR"; mkdir -p "$SRC_DIR"
    tar -C "$SCRIPT_DIR" --exclude=.git --exclude=node_modules --exclude=bin --exclude=obj -cf - . \
      | tar -C "$SRC_DIR" -xf -
  else
    info "Cloning ${REPO_URL} (${REPO_REF})..."
    rm -rf "$SRC_DIR"
    git clone --depth=1 --branch "$REPO_REF" "$REPO_URL" "$SRC_DIR" -q \
      || error "Could not clone ${REPO_URL}. If it is private, clone it to ${SRC_DIR} by hand and re-run."
  fi
fi

[[ -f "${SRC_DIR}/backend/Knight.slnx" ]] || error "${SRC_DIR} does not look like a KNIGHT checkout."
success "Source ready at ${SRC_DIR}"

# ══════════════════════════════════════════════════════════════════════════════
#  PostgreSQL — one role and one database, and nothing else on the server
# ══════════════════════════════════════════════════════════════════════════════

step "PostgreSQL"

psql_super() { runuser -u postgres -- psql -v ON_ERROR_STOP=1 "$@"; }

if ! psql_super -tAc "SELECT 1" >/dev/null 2>&1; then
  info "No local PostgreSQL is reachable — installing one..."
  apt-get install -y -qq postgresql postgresql-client || error "Could not install PostgreSQL."
  systemctl enable --now postgresql >/dev/null 2>&1
  sleep 2
  psql_super -tAc "SELECT 1" >/dev/null 2>&1 \
    || error "PostgreSQL was installed but is not answering. Check: systemctl status postgresql"
  success "PostgreSQL installed"
else
  success "Using the PostgreSQL already on this server: $(psql_super -tAc 'SHOW server_version')"
fi

DB_HOST="127.0.0.1"
DB_PORT="$(psql_super -tAc 'SHOW port' | tr -d '[:space:]')"
[[ -n "$DB_PORT" ]] || DB_PORT=5432

command -v pg_dump >/dev/null || apt-get install -y -qq postgresql-client >/dev/null 2>&1

# Only ours if the role has not been renamed since. A stored password belongs to
# the role it was generated for, and reusing it against a different one gets as
# far as a connection failure nobody can explain.
DB_PASSWORD=""
[[ -n "$STORED_DB_USER" && "$DB_USER" == "$STORED_DB_USER" ]] && DB_PASSWORD="${PREV_DB_PASSWORD:-}"

while true; do
  role_exists="$(psql_super -tAc "SELECT 1 FROM pg_roles WHERE rolname = '${DB_USER}'" 2>/dev/null | tr -d '[:space:]')"

  if [[ "$role_exists" != "1" ]]; then
    DB_PASSWORD="$(random_secret 40)"
    psql_super -qc "CREATE ROLE \"${DB_USER}\" LOGIN PASSWORD '${DB_PASSWORD}';" \
      || error "Could not create the ${DB_USER} role."
    success "Created the ${DB_USER} role"
    break
  fi

  # Ours from a previous run: the stored password still works, nothing to do.
  if [[ -n "$DB_PASSWORD" ]]; then
    success "Reusing the existing ${DB_USER} role"
    break
  fi

  # Somebody else's, or ours with the password lost. Resetting it blindly is how
  # an installer takes another application's database down, so it is not done
  # without being asked for.
  echo ""
  warn "A PostgreSQL role named '${DB_USER}' already exists and this installer does not know its password."
  echo -e "  ${DIM}It may belong to another application on this server. Resetting its${NC}"
  echo -e "  ${DIM}password would break that application's connections.${NC}"
  echo ""
  if [[ "$ASSUME_YES" == "1" ]]; then
    error "Role '${DB_USER}' already exists. Set KNIGHT_DB_USER to an unused name and re-run."
  fi
  ask DB_ROLE_CHOICE "Type a different role name, or 'reset' to take over this one" ""
  if [[ "$DB_ROLE_CHOICE" == "reset" ]]; then
    DB_PASSWORD="$(random_secret 40)"
    psql_super -qc "ALTER ROLE \"${DB_USER}\" WITH LOGIN PASSWORD '${DB_PASSWORD}';" \
      || error "Could not reset the ${DB_USER} role's password."
    warn "The password of role '${DB_USER}' was reset."
    break
  elif [[ -n "$DB_ROLE_CHOICE" ]]; then
    DB_USER="$DB_ROLE_CHOICE"
  fi
done

db_exists="$(psql_super -tAc "SELECT 1 FROM pg_database WHERE datname = '${DB_NAME}'" 2>/dev/null | tr -d '[:space:]')"
if [[ "$db_exists" != "1" ]]; then
  runuser -u postgres -- createdb -O "$DB_USER" "$DB_NAME" || error "Could not create the ${DB_NAME} database."
  success "Created the ${DB_NAME} database, owned by ${DB_USER}"
else
  success "Using the existing ${DB_NAME} database"
fi

DB_CONNECTION="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASSWORD}"

PGPASSWORD="$DB_PASSWORD" psql -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -tAc "SELECT 1" >/dev/null 2>&1 \
  || error "$(cat <<EOF
KNIGHT cannot log in to ${DB_NAME} as ${DB_USER} over TCP.

The role and database exist, so this is pg_hba.conf: it needs a line allowing
password authentication from 127.0.0.1, which is the Debian and Ubuntu default:

  host    all    all    127.0.0.1/32    scram-sha-256
EOF
)"
success "Connection verified"

# ══════════════════════════════════════════════════════════════════════════════
#  Redis — a dedicated instance, not a shared one
# ══════════════════════════════════════════════════════════════════════════════

step "Redis"

# Outside Development, KNIGHT refuses to start without Redis: replay protection
# for store handshakes cannot be in-process across more than one worker
# (docs/adr/0020). It gets an instance of its own rather than a database index
# on a shared one, because a shared server means a shared FLUSHALL, a shared
# eviction policy and a shared memory ceiling — three ways for KNIGHT and its
# neighbour to break each other.
REDIS_PASSWORD="${KNIGHT_REDIS_PASSWORD:-}"
[[ -z "$REDIS_PASSWORD" ]] && REDIS_PASSWORD="$(random_secret 40)"

cat > "${REDIS_DIR}/redis.conf" <<EOF
# KNIGHT's own Redis. Managed by knight-redis.service and by nothing else.

port ${REDIS_PORT}
bind 127.0.0.1 -::1
protected-mode yes
requirepass ${REDIS_PASSWORD}

dir ${REDIS_DIR}
pidfile ${REDIS_DIR}/knight-redis.pid
logfile ""

# What is kept here is a cache, a set of handshake nonces and a set of
# idempotency keys. All three are rebuilt or re-earned after a restart, and
# writing them to disk would buy nothing but I/O.
save ""
appendonly no

# A ceiling, so a fault in KNIGHT cannot take the memory another application on
# this server is relying on. noeviction rather than an LRU policy on purpose:
# silently dropping a handshake nonce is silently weakening replay protection,
# so a full instance fails loudly instead.
maxmemory 256mb
maxmemory-policy noeviction
EOF

chown -R "${SERVICE_USER}:${SERVICE_USER}" "$REDIS_DIR"
chmod 600 "${REDIS_DIR}/redis.conf"

cat > /etc/systemd/system/knight-redis.service <<EOF
[Unit]
Description=KNIGHT Redis
After=network.target
Before=knight-api.service

[Service]
Type=notify
User=${SERVICE_USER}
Group=${SERVICE_USER}
ExecStart=$(command -v redis-server) ${REDIS_DIR}/redis.conf --supervised systemd
Restart=always
RestartSec=5
SyslogIdentifier=knight-redis

NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=strict
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
ReadWritePaths=${REDIS_DIR}

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable knight-redis >/dev/null 2>&1
systemctl restart knight-redis

for _ in $(seq 1 15); do
  systemctl is-active --quiet knight-redis && break
  sleep 1
done
systemctl is-active --quiet knight-redis \
  || error "knight-redis did not start. Diagnose: journalctl -u knight-redis -n 40 --no-pager"
success "Dedicated Redis running on 127.0.0.1:${REDIS_PORT}"

REDIS_CONNECTION="127.0.0.1:${REDIS_PORT},password=${REDIS_PASSWORD},abortConnect=false"

# ══════════════════════════════════════════════════════════════════════════════
#  nginx and TLS
# ══════════════════════════════════════════════════════════════════════════════

step "nginx and TLS"

# The only thing that has to live in the http{} context, and both names are
# prefixed so this file can never collide with a directive another application
# on this server already declared. A duplicate map variable or a duplicate
# limit_req_zone makes nginx refuse to load *every* site, not just ours.
cat > /etc/nginx/conf.d/knight-shared.conf <<'EOF'
# KNIGHT. Removed by `knightctl uninstall`.

# SignalR upgrades to a WebSocket, which needs the Connection header rewritten
# per request rather than pinned to "upgrade".
map $http_upgrade $knight_connection_upgrade {
    default upgrade;
    ''      close;
}

limit_req_zone $binary_remote_addr zone=knight_login:10m rate=10r/m;
EOF

# One hostname for the dashboard, the API, the realtime hub and the artifact
# downloads agents fetch. The dashboard bundle addresses all of them with
# relative URLs, so nothing in it has to be rebuilt if the domain changes.
cat > /etc/nginx/sites-available/knight <<EOF
# KNIGHT. Written by install.sh; \`knightctl domain\` rewrites it.
server {
    listen 80;
    listen [::]:80;
    server_name ${DOMAIN};

    root ${DASHBOARD_DIR};
    index index.html;

    # Kept in this server block rather than in conf.d so that nothing here can
    # clash with the global settings of another application on this server.
    gzip on;
    gzip_vary on;
    gzip_comp_level 6;
    gzip_types text/plain text/css text/javascript application/javascript application/json image/svg+xml;

    add_header X-Frame-Options "DENY" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;

    # The dashboard uploads signed Feature packages and base store images.
    client_max_body_size 512m;

    # Sign-in is the credential-guessing surface. This sits in front of KNIGHT's
    # own lockout and its own rate limiter rather than replacing either.
    location = /api/v1/auth/login {
        limit_req zone=knight_login burst=5 nodelay;

        proxy_pass         http://127.0.0.1:${APP_PORT};
        proxy_http_version 1.1;
        proxy_set_header   Host              \$host;
        proxy_set_header   X-Real-IP         \$remote_addr;
        proxy_set_header   X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto \$scheme;
    }

    location ^~ /api/ {
        proxy_pass         http://127.0.0.1:${APP_PORT};
        proxy_http_version 1.1;
        proxy_set_header   Host              \$host;
        proxy_set_header   X-Real-IP         \$remote_addr;
        proxy_set_header   X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto \$scheme;
        proxy_read_timeout 120s;
    }

    # The realtime channel. Without the two upgrade headers the handshake falls
    # back to long polling and quietly costs a connection per dashboard tab.
    location ^~ /hubs/ {
        proxy_pass         http://127.0.0.1:${APP_PORT};
        proxy_http_version 1.1;
        proxy_set_header   Upgrade           \$http_upgrade;
        proxy_set_header   Connection        \$knight_connection_upgrade;
        proxy_set_header   Host              \$host;
        proxy_set_header   X-Real-IP         \$remote_addr;
        proxy_set_header   X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto \$scheme;

        # A websocket that is idle between events must not be closed under it.
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    # Where an agent fetches a signed Feature package from. Served by the API,
    # which checks the link's expiry — never from the filesystem directly.
    location ^~ /artifacts/ {
        proxy_pass         http://127.0.0.1:${APP_PORT};
        proxy_http_version 1.1;
        proxy_set_header   Host              \$host;
        proxy_set_header   X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto \$scheme;
        proxy_read_timeout 300s;
    }

    location ^~ /health/ {
        proxy_pass       http://127.0.0.1:${APP_PORT};
        access_log       off;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }

    # Vite emits content-hashed filenames, so these can never go stale.
    location /assets/ {
        expires 30d;
        add_header Cache-Control "public, immutable";
        try_files \$uri =404;
    }

    location / {
        try_files \$uri \$uri/ /index.html;
    }
}
EOF

ln -sf /etc/nginx/sites-available/knight /etc/nginx/sites-enabled/knight

# Deliberately NOT removing sites-enabled/default or anybody else's site. This
# server block names its hostname, so it is chosen for that hostname and leaves
# every other site on the machine answering exactly as it did before.
if nginx -t >/dev/null 2>&1; then
  systemctl reload nginx 2>/dev/null || systemctl restart nginx
  success "nginx site written and loaded"
else
  nginx -t
  error "nginx rejected the configuration — see the output above. Nothing else was changed."
fi

SCHEME="http"
if certbot --nginx -d "$DOMAIN" --non-interactive --agree-tos --email "$SSL_EMAIL" --redirect >/dev/null 2>&1; then
  SCHEME="https"
  success "Certificate issued for ${DOMAIN}"
else
  warn "Certificate could not be issued for ${DOMAIN}."
  warn "The usual cause is DNS: the A record has to point at this server and have propagated."
  warn "Once it does:  certbot --nginx -d ${DOMAIN} --redirect"
fi

systemctl enable certbot.timer >/dev/null 2>&1 \
  || (crontab -l 2>/dev/null; echo "0 3 * * * certbot renew --quiet") | crontab -

# ══════════════════════════════════════════════════════════════════════════════
#  Build
# ══════════════════════════════════════════════════════════════════════════════

step "Build"

# Publishing over a running host's own files fails on Linux as surely as it does
# anywhere else, and leaves a half-written directory behind.
systemctl stop knight-api >/dev/null 2>&1

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export PATH="${NODE_BIN}:${PATH}"

# --- Dashboard ----------------------------------------------------------------

# Both URL variables are deliberately absent. Left unset, the bundle addresses
# the API at /api/v1 and the hub at /hubs/control-plane — relative, so it works
# over http and https alike, and survives a change of domain without a rebuild.
cat > "${SRC_DIR}/frontend/knight-dashboard/.env.production" <<'EOF'
VITE_USE_MOCKS=false
VITE_DEFAULT_LOCALE=fa
EOF

info "Installing dashboard packages..."
( cd "${SRC_DIR}/frontend/knight-dashboard" && npm ci --no-audit --no-fund ) 2>&1 | tail -3

info "Building the dashboard..."
( cd "${SRC_DIR}/frontend/knight-dashboard" && npm run build ) 2>&1 | tail -5

[[ -f "${SRC_DIR}/frontend/knight-dashboard/dist/index.html" ]] \
  || error "The dashboard build produced no dist/index.html."

rm -rf "${DASHBOARD_DIR:?}"/*
cp -r "${SRC_DIR}/frontend/knight-dashboard/dist/." "$DASHBOARD_DIR/"
success "Dashboard built"

# --- API and the bootstrap tool -----------------------------------------------

info "Publishing the API (a few minutes on a first run)..."
"$DOTNET_EXEC" publish "${SRC_DIR}/backend/src/Knight.Api/Knight.Api.csproj" \
  -c Release -o "$API_DIR" --nologo 2>&1 | tail -3
[[ -f "${API_DIR}/Knight.Api.dll" ]] || error "The API build produced no Knight.Api.dll."

# The development settings travel with the publish output and hold the
# placeholder signing key the host refuses in Production. Nothing reads them
# here, and a file of stale secrets on a server is worth one line to delete.
rm -f "${API_DIR}/appsettings.Development.json"

info "Publishing the bootstrap tool..."
"$DOTNET_EXEC" publish "${SRC_DIR}/backend/tools/Knight.Bootstrap/Knight.Bootstrap.csproj" \
  -c Release -o "$BOOTSTRAP_DIR" --nologo 2>&1 | tail -3
[[ -f "${BOOTSTRAP_DIR}/Knight.Bootstrap.dll" ]] || error "The bootstrap tool did not build."
success "API and bootstrap tool published"

# ══════════════════════════════════════════════════════════════════════════════
#  Configuration
# ══════════════════════════════════════════════════════════════════════════════

step "Configuration"

# Secrets already in place are kept. Rotating the token key on a re-install would
# sign every logged-in administrator out, and rotating the store key would
# invalidate the entitlement payloads every connected store is holding.
JWT_SIGNING_KEY="${Jwt__SigningKey:-}"
[[ -z "$JWT_SIGNING_KEY" ]] && JWT_SIGNING_KEY="$(random_secret 64)"

# A separate key on purpose. One leak must not compromise both the tokens that
# authenticate administrators and the payloads stores cache and trust
# (docs/authentication.md section 5).
STORE_SIGNING_KEY="${Stores__IntegrationSigningKey:-}"
[[ -z "$STORE_SIGNING_KEY" ]] && STORE_SIGNING_KEY="$(random_secret 64)"

# Every signing key this deployment has ever been given, not only the active
# one. A retired key still has to verify the versions it signed, and this file
# is rewritten from scratch below - so without carrying them across, a re-install
# would quietly make every already-published Feature version unverifiable.
PRESERVED_SIGNING_KEYS=""
if [[ -s "$ENV_FILE" ]]; then
  PRESERVED_SIGNING_KEYS="$(grep '^FeatureArtifacts__Keys__' "$ENV_FILE" || true)"
fi

: > "$ENV_FILE"
chmod 600 "$ENV_FILE"

{
  echo "# KNIGHT runtime configuration and secrets."
  echo "# Written by install.sh. Read by knight-api.service and by knightctl."
  echo "# Owner-only, and never to be committed anywhere."
  echo ""
} >> "$ENV_FILE"

[[ -n "$PRESERVED_SIGNING_KEYS" ]] && printf '%s
' "$PRESERVED_SIGNING_KEYS" >> "$ENV_FILE"

write_env ASPNETCORE_ENVIRONMENT "Production"
write_env ASPNETCORE_URLS "http://127.0.0.1:${APP_PORT}"
write_env DOTNET_CLI_TELEMETRY_OPTOUT "1"

# Data Protection wants somewhere to keep its keys, and the service unit denies
# it everywhere else.
write_env HOME "$STATE_DIR"

write_env ConnectionStrings__ControlPlane "$DB_CONNECTION"
write_env ConnectionStrings__Redis "$REDIS_CONNECTION"

write_env Jwt__SigningKey "$JWT_SIGNING_KEY"
write_env Jwt__Issuer "knight-control-plane"
write_env Jwt__Audience "knight-dashboard"

write_env Stores__IntegrationSigningKey "$STORE_SIGNING_KEY"

# nginx on this machine. Named explicitly rather than left to the framework's
# defaults, which do not recognise a plain IPv4 loopback.
write_env ForwardedHeaders__KnownProxies__0 "127.0.0.1"
write_env ForwardedHeaders__KnownProxies__1 "::1"

# Same origin as the dashboard, so this list is belt and braces rather than the
# thing that makes the dashboard work.
write_env Cors__AllowedOrigins__0 "${SCHEME}://${DOMAIN}"

write_env FeatureArtifacts__ArtifactRoot "$ARTIFACT_DIR"
write_env FeatureArtifacts__PublicBaseUrl "${SCHEME}://${DOMAIN}/artifacts"
if [[ -n "$SIGNING_PUBLIC_KEY" ]]; then
  write_env FeatureArtifacts__ActiveKeyId "$SIGNING_KEY_ID"
  write_env "FeatureArtifacts__Keys__${SIGNING_KEY_ID}__PublicKey" "$SIGNING_PUBLIC_KEY"
elif [[ -n "$STORED_ACTIVE_KEY_ID" ]]; then
  write_env FeatureArtifacts__ActiveKeyId "$STORED_ACTIVE_KEY_ID"
fi

write_env Storage__LocalRootPath "$STORAGE_DIR"

if [[ -n "$SMTP_HOST" ]]; then
  write_env Email__Host "$SMTP_HOST"
  write_env Email__Port "$SMTP_PORT"
  write_env Email__FromAddress "$SMTP_FROM"
  write_env Email__FromName "KNIGHT"
  write_env Email__DashboardBaseUrl "${SCHEME}://${DOMAIN}"
  [[ -n "$SMTP_USER" ]] && write_env Email__Username "$SMTP_USER"
  [[ -n "$SMTP_PASSWORD" ]] && write_env Email__Password "$SMTP_PASSWORD"
fi

# Bookkeeping. Not read by the API — this is what knightctl and a re-run of this
# script use to find what the last one decided.
write_env KNIGHT_DOMAIN "$DOMAIN"
write_env KNIGHT_SCHEME "$SCHEME"
write_env KNIGHT_APP_PORT "$APP_PORT"
write_env KNIGHT_REDIS_PORT "$REDIS_PORT"
write_env KNIGHT_REDIS_PASSWORD "$REDIS_PASSWORD"
write_env KNIGHT_DB_HOST "$DB_HOST"
write_env KNIGHT_DB_PORT "$DB_PORT"
write_env KNIGHT_DB_NAME "$DB_NAME"
write_env KNIGHT_DB_USER "$DB_USER"
write_env KNIGHT_DB_PASSWORD "$DB_PASSWORD"
write_env KNIGHT_DOTNET "$DOTNET_EXEC"
write_env KNIGHT_NODE_BIN "$NODE_BIN"
write_env KNIGHT_SSL_EMAIL "$SSL_EMAIL"
write_env KNIGHT_REPO_URL "$REPO_URL"
write_env KNIGHT_REPO_REF "$REPO_REF"

success "Configuration written to ${ENV_FILE}"

# --- Permissions --------------------------------------------------------------

chown -R "${SERVICE_USER}:${SERVICE_USER}" "$INSTALL_DIR"

# nginx serves the bundle as www-data, so it needs to traverse the install root
# and read the dashboard — and nothing else under it.
chmod 755 "$INSTALL_DIR" "$DASHBOARD_DIR"
find "$DASHBOARD_DIR" -type d -exec chmod 755 {} +
find "$DASHBOARD_DIR" -type f -exec chmod 644 {} +

chmod 750 "$APP_DIR" "$SRC_DIR" "$ARTIFACT_DIR" "$STORAGE_DIR" "$TOOLCHAIN_DIR"
chmod 700 "$REDIS_DIR" "$STATE_DIR" "$BACKUP_DIR"
chmod 600 "$ENV_FILE" "${REDIS_DIR}/redis.conf"
success "Permissions set"

# ══════════════════════════════════════════════════════════════════════════════
#  Database schema
# ══════════════════════════════════════════════════════════════════════════════

step "Database schema"

# The API host deliberately does not migrate itself: that is a deployment step,
# and this is the deployment (docs/adr/0018). It is idempotent, so it runs on
# every install and most of the time has nothing to do.
info "Applying migrations and reconciling seed data..."
CONTROL_PLANE_DB_CONNECTION_STRING="$DB_CONNECTION" \
  "$DOTNET_EXEC" "${BOOTSTRAP_DIR}/Knight.Bootstrap.dll" --migrate-only 2>&1 | tail -5

CONTROL_PLANE_DB_CONNECTION_STRING="$DB_CONNECTION" \
  "$DOTNET_EXEC" "${BOOTSTRAP_DIR}/Knight.Bootstrap.dll" --migrate-only >/dev/null 2>&1 \
  || error "Migrations did not apply. The API has not been started."
success "Schema up to date"

# ══════════════════════════════════════════════════════════════════════════════
#  Service
# ══════════════════════════════════════════════════════════════════════════════

step "Service"

cat > /etc/systemd/system/knight-api.service <<EOF
[Unit]
Description=KNIGHT control plane
After=network-online.target knight-redis.service postgresql.service
Wants=network-online.target
Requires=knight-redis.service

[Service]
Type=simple
User=${SERVICE_USER}
Group=${SERVICE_USER}
WorkingDirectory=${API_DIR}
EnvironmentFile=${ENV_FILE}
ExecStart=${DOTNET_EXEC} ${API_DIR}/Knight.Api.dll
Restart=always
RestartSec=5
SyslogIdentifier=knight-api

# The point of most of this is the neighbours. KNIGHT can read nothing outside
# its own directory, write nothing outside the four paths it genuinely needs,
# and gain no privilege it was not started with — so a fault here stays here.
NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=strict
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
RestrictNamespaces=true
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
LockPersonality=true
ReadWritePaths=${ARTIFACT_DIR} ${STORAGE_DIR} ${STATE_DIR}

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable knight-api >/dev/null 2>&1
systemctl restart knight-api

info "Waiting for the API..."
API_OK=false
for _ in $(seq 1 45); do
  if curl -fsS --max-time 3 "http://127.0.0.1:${APP_PORT}/health/ready" >/dev/null 2>&1; then
    API_OK=true
    break
  fi
  systemctl is-active --quiet knight-api || break
  sleep 1
done

if $API_OK; then
  success "The API is up and reports ready"
else
  warn "The API did not report ready within 45 seconds."
  warn "Diagnose with:  journalctl -u knight-api -n 60 --no-pager"
fi

# ══════════════════════════════════════════════════════════════════════════════
#  First administrator
# ══════════════════════════════════════════════════════════════════════════════

if [[ -n "$ADMIN_EMAIL" ]]; then
  step "First administrator"

  # Never as an argument: an argument is in the shell history, in the process
  # list, and in anything watching either. The tool reads it from stdin, twice,
  # exactly as it does when a person types it.
  if printf '%s\n%s\n' "$ADMIN_PASSWORD" "$ADMIN_PASSWORD" \
    | CONTROL_PLANE_DB_CONNECTION_STRING="$DB_CONNECTION" \
      "$DOTNET_EXEC" "${BOOTSTRAP_DIR}/Knight.Bootstrap.dll" --email "$ADMIN_EMAIL" 2>&1 | tail -4
  then
    success "Administrator ${ADMIN_EMAIL} created"
  else
    warn "The administrator could not be created. Create one with:  knightctl admin"
  fi
fi
unset ADMIN_PASSWORD

# ══════════════════════════════════════════════════════════════════════════════
#  Nightly backup
# ══════════════════════════════════════════════════════════════════════════════

step "Nightly backup"

cat > /etc/systemd/system/knight-backup.service <<EOF
[Unit]
Description=KNIGHT control-plane database backup
After=postgresql.service

[Service]
Type=oneshot
User=${SERVICE_USER}
Group=${SERVICE_USER}
Environment=PGHOST=${DB_HOST}
Environment=PGPORT=${DB_PORT}
Environment=PGUSER=${DB_USER}
Environment=PGPASSWORD=${DB_PASSWORD}
Environment=KNIGHT_DB=${DB_NAME}
Environment=KNIGHT_BACKUP_DIR=${BACKUP_DIR}
ExecStart=${SRC_DIR}/infrastructure/scripts/knight-backup.sh
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=${BACKUP_DIR}
EOF

cat > /etc/systemd/system/knight-backup.timer <<'EOF'
[Unit]
Description=Nightly KNIGHT control-plane backup

[Timer]
OnCalendar=*-*-* 02:30:00
# A server that was asleep or off at 02:30 still gets its backup.
Persistent=true
RandomizedDelaySec=600

[Install]
WantedBy=timers.target
EOF

chmod +x "${SRC_DIR}/infrastructure/scripts/"*.sh 2>/dev/null
systemctl daemon-reload
systemctl enable --now knight-backup.timer >/dev/null 2>&1
success "Nightly backup scheduled for 02:30 into ${BACKUP_DIR}"
warn "A backup on the same machine is not a backup. Copy ${BACKUP_DIR} somewhere else."

# ══════════════════════════════════════════════════════════════════════════════
#  Management tool
# ══════════════════════════════════════════════════════════════════════════════

step "Management tool"

if [[ -f "${SRC_DIR}/knightctl.sh" ]]; then
  install -m 755 "${SRC_DIR}/knightctl.sh" /usr/local/bin/knightctl
  success "knightctl installed — run it any time to manage this deployment"
else
  warn "knightctl.sh was not in the checkout; skipping the management tool."
fi

# ══════════════════════════════════════════════════════════════════════════════
#  Done
# ══════════════════════════════════════════════════════════════════════════════

echo ""
echo -e "${GREEN}${BOLD}"
echo "  ╔═══════════════════════════════════════════╗"
echo "  ║           KNIGHT is installed             ║"
echo "  ╚═══════════════════════════════════════════╝"
echo -e "${NC}"
echo -e "  Dashboard   ${CYAN}${SCHEME}://${DOMAIN}${NC}"
[[ -n "$ADMIN_EMAIL" ]] && \
echo -e "  Sign in as  ${CYAN}${ADMIN_EMAIL}${NC} ${DIM}(the password you chose)${NC}"
echo ""
echo -e "  ${BOLD}First sign-in${NC}"
echo -e "  That account holds SuperAdmin, so it will ask you to enrol an"
echo -e "  authenticator app before it can reach anything else. Then, in the"
echo -e "  dashboard: create a customer, register their store, issue a store"
echo -e "  credential, and connect the store to this control plane."
echo ""
echo -e "  ${BOLD}Managing it${NC}"
echo -e "  ${CYAN}knightctl${NC}                  status, logs, backup, update, domain, uninstall"
echo -e "  ${CYAN}journalctl -u knight-api -f${NC} live API logs"
echo ""
echo -e "  ${BOLD}Where things are${NC}"
echo -e "  ${DIM}${INSTALL_DIR}${NC}              everything KNIGHT owns"
echo -e "  ${DIM}${ENV_FILE}${NC}     configuration and secrets, owner-only"
echo -e "  ${DIM}${BACKUP_DIR}${NC}      nightly database dumps"
echo ""

if [[ -z "$SIGNING_PUBLIC_KEY" && -z "$STORED_ACTIVE_KEY_ID" ]]; then
  warn "No artifact signing key is configured, so Feature versions cannot be published yet."
  echo -e "  ${DIM}Set one with:  knightctl signing-key${NC}"
  echo ""
fi

if [[ "$SCHEME" == "http" ]]; then
  warn "This deployment is on plain HTTP. Issue the certificate once DNS points here:"
  echo -e "  ${BOLD}certbot --nginx -d ${DOMAIN} --redirect && knightctl domain ${DOMAIN}${NC}"
  echo ""
fi

if ! $API_OK; then
  echo -e "  ${RED}The API is not answering. Start here: journalctl -u knight-api -n 60 --no-pager${NC}"
  echo ""
fi

# Explicit, because an installer's exit status is what a provisioning system
# reads and this script deliberately warns rather than aborts for things an
# operator can fix afterwards. A missing certificate is one of those. An API
# that never answered is not.
$API_OK || exit 1
exit 0
