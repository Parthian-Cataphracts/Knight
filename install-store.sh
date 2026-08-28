#!/usr/bin/env bash
#
# Installs a Django store on a managed server, in the shape the agent expects.
#
# The third installer. `install.sh` puts the control plane somewhere,
# `install-agent.sh` puts an agent on a server that hosts stores, and this puts a
# store on one — which was the step everybody did by hand: a directory, a
# virtualenv, an environment file, a database, migrations, a unit, a socket.
#
# Two things here are not conveniences.
#
# **The store is laid out where the agent looks.** `KNIGHT_FEATURE_ROOT` is the
# directory delivered Features land in, and it lives beside the store rather than
# inside it: an install that unpacked a Feature into the source tree would lose
# every Feature the next time somebody deployed the store.
#
# **A restart does not drop what is in flight.** The unit is socket-activated,
# so the socket outlives the service: `systemctl reload` starts new workers and
# lets the old ones finish what they are holding, and a request that arrived a
# millisecond before a deploy is answered rather than reset. That was carried
# from phase 3.5 as "the installer writes a unit and restarting it drops
# whatever was in flight", and it is the difference between deploying at three
# in the morning and deploying at three in the afternoon.
#
# Usage:
#   sudo ./install-store.sh --name cafe-parthia \
#                           --source /srv/src/reference-store \
#                           --domain shop.example.com \
#                           --db-name cafe --db-user store --db-password secret
#
# Re-running it upgrades the code in place and keeps the environment file, the
# delivered Features and the database exactly as they are.

set -euo pipefail

NAME=""
SOURCE=""
DOMAIN=""
DB_NAME=""
DB_USER=""
DB_PASSWORD=""
DB_HOST="127.0.0.1"
DB_PORT="5432"
PORT=""
ROOT="/srv/stores"
SERVICE_USER=""

BOLD=$'\033[1m'; RESET=$'\033[0m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'

step()    { echo; echo "${BOLD}==> $*${RESET}"; }
success() { echo "  ${GREEN}ok${RESET}  $*"; }
warn()    { echo "  ${YELLOW}!!${RESET}  $*"; }
fail()    { echo "  ${RED}xx${RESET}  $*" >&2; exit 1; }

usage() {
  sed -n '2,32p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --name)   NAME="${2:-}"; shift 2 ;;
    --source) SOURCE="${2:-}"; shift 2 ;;
    --domain) DOMAIN="${2:-}"; shift 2 ;;
    --db-name)     DB_NAME="${2:-}"; shift 2 ;;
    --db-user)     DB_USER="${2:-}"; shift 2 ;;
    --db-password) DB_PASSWORD="${2:-}"; shift 2 ;;
    --db-host)     DB_HOST="${2:-}"; shift 2 ;;
    --db-port)     DB_PORT="${2:-}"; shift 2 ;;
    --port)   PORT="${2:-}"; shift 2 ;;
    --root)   ROOT="${2:-}"; shift 2 ;;
    -h|--help) usage 0 ;;
    *) echo "Unknown option: $1" >&2; usage 1 ;;
  esac
done

[[ $EUID -eq 0 ]] || fail "Run this with sudo: it creates a user, a directory under ${ROOT} and a systemd unit."
[[ -n "$NAME" ]] || fail "--name is required. It names the directory, the user and the units."
[[ -n "$SOURCE" ]] || fail "--source is required. It is the store's source tree."
[[ -d "$SOURCE" ]] || fail "'${SOURCE}' is not a directory."
[[ -f "${SOURCE}/manage.py" ]] || fail "'${SOURCE}' has no manage.py. This installer is for a Django store."
[[ "$NAME" =~ ^[a-z0-9][a-z0-9-]*$ ]] || fail "--name must be lowercase letters, digits and hyphens: it becomes a user and a unit name."

command -v systemctl >/dev/null || fail "This installer writes systemd units and this machine has no systemctl."
command -v python3 >/dev/null || fail "python3 is not on PATH."

SERVICE_USER="store-${NAME}"
STORE_DIR="${ROOT}/${NAME}"
APP_DIR="${STORE_DIR}/app"
VENV_DIR="${STORE_DIR}/venv"
FEATURE_ROOT="${STORE_DIR}/features"
ENV_FILE="${STORE_DIR}/store.env"
SOCKET="/run/store-${NAME}.sock"

# ── Layout ───────────────────────────────────────────────────────────────────

step "Layout"

if id "$SERVICE_USER" >/dev/null 2>&1; then
  success "${SERVICE_USER} already exists"
else
  useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
  success "created ${SERVICE_USER}"
fi

install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 750 "$STORE_DIR" "$APP_DIR" "$FEATURE_ROOT"
success "${STORE_DIR}"

# Beside the app, never inside it. A Feature unpacked into the source tree is a
# Feature that disappears the next time this installer copies a new one in.
success "features land in ${FEATURE_ROOT}"

# ── Code ─────────────────────────────────────────────────────────────────────

step "Code"

# --delete so a file removed upstream is removed here, with the two directories
# that are not the deploy's to touch left alone: the features are delivered and
# the environment file is the operator's.
rsync --archive --delete \
  --exclude "features/" \
  --exclude "store.env" \
  --exclude ".git/" \
  --exclude "__pycache__/" \
  "${SOURCE}/" "${APP_DIR}/"

chown -R "$SERVICE_USER":"$SERVICE_USER" "$APP_DIR"
success "copied $(find "$APP_DIR" -name '*.py' | wc -l | tr -d ' ') Python file(s)"

python3 -m venv "$VENV_DIR" >/dev/null
"${VENV_DIR}/bin/pip" install --quiet --upgrade pip >/dev/null
"${VENV_DIR}/bin/pip" install --quiet -r "${APP_DIR}/requirements.txt" >/dev/null

# The WSGI server is the installer's choice rather than the store's: a store's
# requirements describe the application, and how it is served is a deployment
# decision that changes without the application changing.
"${VENV_DIR}/bin/pip" install --quiet gunicorn >/dev/null
chown -R "$SERVICE_USER":"$SERVICE_USER" "$VENV_DIR"
success "dependencies installed"

# ── Environment ──────────────────────────────────────────────────────────────

step "Environment"

if [[ -f "$ENV_FILE" ]]; then
  # Kept. It holds the store's KNIGHT credential and its database password, and
  # an installer that rewrote it on every run would disconnect a working store
  # in the name of upgrading it.
  success "kept the existing ${ENV_FILE}"
else
  # The four database settings by name rather than one URL, because that is
  # what the store's own settings read. An installer that wrote a DATABASE_URL
  # nothing looks at would produce a store that starts, connects to the default
  # database, and is wrong in a way nobody notices until two shops share one.
  [[ -n "$DB_NAME" && -n "$DB_USER" ]] \
    || fail "--db-name and --db-user are required the first time: the store needs a database."

  umask 077
  cat > "$ENV_FILE" <<EOF
# ${NAME} — written by install-store.sh. Owner-only, and it holds credentials.
DJANGO_SECRET_KEY=$(python3 -c 'import secrets; print(secrets.token_urlsafe(48))')
DJANGO_DEBUG=false
DJANGO_ALLOWED_HOSTS=${DOMAIN:-localhost}
STORE_DB_NAME=${DB_NAME}
STORE_DB_USER=${DB_USER}
STORE_DB_PASSWORD=${DB_PASSWORD}
STORE_DB_HOST=${DB_HOST}
STORE_DB_PORT=${DB_PORT}
KNIGHT_FEATURE_ROOT=${FEATURE_ROOT}

# Filled in once KNIGHT has issued this store a credential:
#   KNIGHT_BASE_URL=https://knight.example.com
#   KNIGHT_CLIENT_ID=
#   KNIGHT_CLIENT_SECRET=
#   KNIGHT_STORE_ID=
EOF
  chown "$SERVICE_USER":"$SERVICE_USER" "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  success "wrote ${ENV_FILE}"
  warn "the store has no KNIGHT credential yet; add one to ${ENV_FILE} and reload"
fi

# ── Database ─────────────────────────────────────────────────────────────────

step "Database"

# The environment file is sourced rather than expanded onto a command line: a
# password with a space in it would otherwise arrive as two arguments, and the
# migration would fail with a message about the database that has nothing to do
# with the database.
if sudo -u "$SERVICE_USER" bash -c "set -a; . '${ENV_FILE}'; set +a; \
     '${VENV_DIR}/bin/python' '${APP_DIR}/manage.py' migrate --noinput" >/dev/null; then
  success "migrations applied"
else
  fail "Migrations failed. The store is installed and not serving; fix the database and re-run."
fi

# ── Serving ──────────────────────────────────────────────────────────────────
#
# A socket unit and a service unit, rather than a service that binds a port.
#
# The socket is owned by systemd and outlives the service, so the reverse proxy
# keeps a connection to something that is always there. Reloading the service
# tells gunicorn to start new workers and let the old ones finish; a request in
# flight is answered by the worker that was already handling it, and one that
# arrives during the swap waits in the socket's own backlog for a few
# milliseconds instead of being refused.

step "Serving"

cat > /etc/systemd/system/store-${NAME}.socket <<EOF
[Unit]
Description=Socket for the ${NAME} store

[Socket]
ListenStream=${PORT:-$SOCKET}
SocketUser=${SERVICE_USER}
SocketGroup=www-data
SocketMode=0660
# Deep enough that a reload's brief pause is a queue rather than a refusal.
Backlog=2048

[Install]
WantedBy=sockets.target
EOF

cat > /etc/systemd/system/store-${NAME}.service <<EOF
[Unit]
Description=${NAME} store
Requires=store-${NAME}.socket
After=network-online.target store-${NAME}.socket

[Service]
Type=notify
User=${SERVICE_USER}
Group=${SERVICE_USER}
WorkingDirectory=${APP_DIR}
EnvironmentFile=${ENV_FILE}
Environment=KNIGHT_FEATURE_ROOT=${FEATURE_ROOT}
ExecStart=${VENV_DIR}/bin/gunicorn config.wsgi:application \\
  --workers 3 \\
  --worker-tmp-dir /dev/shm \\
  --graceful-timeout 30 \\
  --timeout 60

# The reload that does not drop traffic. gunicorn takes HUP as "start new
# workers, retire the old ones once they are idle", which is why this is a
# reload and not a restart: a restart closes the listening socket, and every
# connection on it goes with it.
ExecReload=/bin/kill -HUP \$MAINPID
KillSignal=SIGTERM
TimeoutStopSec=45
Restart=always
RestartSec=5
SyslogIdentifier=store-${NAME}

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
ReadWritePaths=${STORE_DIR}

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "store-${NAME}.socket" >/dev/null 2>&1

if systemctl is-active --quiet "store-${NAME}.service"; then
  # Reload rather than restart, on an upgrade of a store that is serving
  # shoppers right now. This is the line the whole unit arrangement is for.
  systemctl reload "store-${NAME}.service"
  success "reloaded without dropping traffic"
else
  systemctl start "store-${NAME}.service"
  success "started"
fi

# ── What an operator needs to know ───────────────────────────────────────────

step "Done"

cat <<SUMMARY

  Store      ${STORE_DIR}
  App        ${APP_DIR}
  Features   ${FEATURE_ROOT}      (delivered here; do not edit by hand)
  Env        ${ENV_FILE}          (owner-only; credentials)
  Listening  ${PORT:-${SOCKET}}

  Reload     systemctl reload store-${NAME}      # no dropped requests
  Logs       journalctl -u store-${NAME} -f

  Point the reverse proxy at ${PORT:-${SOCKET}} and terminate TLS there.

  This machine's agent claims the store's delivery jobs. If it is not already
  managing this directory, add it:

      sudo ./install-agent.sh --base-url <knight> --store ${APP_DIR}

SUMMARY
