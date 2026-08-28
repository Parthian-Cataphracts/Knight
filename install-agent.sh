#!/usr/bin/env bash
#
# Installs the KNIGHT agent on a server that hosts stores.
#
# The other half of `install.sh`. That one puts the control plane on a machine;
# this one puts an agent on the machines the control plane delivers to, and
# until now it was a README somebody followed by hand — a pip install, a user, a
# unit file, four paths and a credential, in the right order, on every server.
# Doing that by hand across a fleet is how two servers end up subtly different
# and only one of them can take delivery of anything.
#
# What it does, and nothing else:
#
#   1. a system user that owns the agent's state and nothing else
#   2. the agent, installed into a virtualenv of its own
#   3. enrolment, if a provisioning token is given — burned on use
#   4. a systemd unit, hardened the way `install.sh` hardens the API
#   5. a status line an operator can read
#
# It is safe to re-run. An existing credential is kept: re-running the installer
# after a package upgrade must not un-enrol a server, because the token that
# enrolled it was one-time and nobody has it any more.
#
# Usage:
#   sudo ./install-agent.sh --base-url https://knight.example.com \
#                           --token <provisioning token> \
#                           --store /srv/stores/cafe-parthia
#
#   --store is repeatable and may be omitted; a server with no stores yet is a
#   server that reports its metrics and claims no jobs, which is a perfectly
#   ordinary state for a machine that was just built.

set -euo pipefail

BASE_URL=""
TOKEN=""
STORES=()
SERVICE_USER="knight-agent"
INSTALL_DIR="/opt/knight-agent"
STATE_DIR="/var/lib/knight-agent"
SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# --- Saying what happened -----------------------------------------------------

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
    --base-url) BASE_URL="${2:-}"; shift 2 ;;
    --token)    TOKEN="${2:-}"; shift 2 ;;
    --store)    STORES+=("${2:-}"); shift 2 ;;
    --user)     SERVICE_USER="${2:-}"; shift 2 ;;
    -h|--help)  usage 0 ;;
    *)          echo "Unknown option: $1" >&2; usage 1 ;;
  esac
done

[[ $EUID -eq 0 ]] || fail "Run this with sudo: it creates a user and a systemd unit."
[[ -n "$BASE_URL" ]] || fail "--base-url is required. It is where this agent reports to."
[[ -d "${SOURCE_DIR}/agent" ]] || fail "Run this from a checkout: ${SOURCE_DIR}/agent is not here."

command -v systemctl >/dev/null || fail "This installer writes a systemd unit and this machine has no systemctl."
command -v python3 >/dev/null || fail "python3 is not on PATH. The agent is a Python program."

# ── The user ─────────────────────────────────────────────────────────────────

step "Service user"

if id "$SERVICE_USER" >/dev/null 2>&1; then
  success "${SERVICE_USER} already exists"
else
  # No login, no home worth the name, no shell. The agent's authority is its
  # credential file; the account is only there so the unit has somebody to be.
  useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
  success "created ${SERVICE_USER}"
fi

install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 750 "$STATE_DIR"
success "state directory ${STATE_DIR}"

# ── The agent ────────────────────────────────────────────────────────────────

step "Agent"

# A virtualenv rather than the system Python. An agent that shared site-packages
# with whatever else the machine runs is an agent that breaks when somebody
# upgrades something unrelated.
python3 -m venv "$INSTALL_DIR" >/dev/null
"${INSTALL_DIR}/bin/pip" install --quiet --upgrade pip >/dev/null
"${INSTALL_DIR}/bin/pip" install --quiet "${SOURCE_DIR}/agent" >/dev/null

AGENT="${INSTALL_DIR}/bin/knight-agent"
[[ -x "$AGENT" ]] || fail "The agent installed and ${AGENT} is not there. Check the package's entry point."

chown -R "$SERVICE_USER":"$SERVICE_USER" "$INSTALL_DIR"
success "installed the agent into ${INSTALL_DIR}"

# ── Enrolment ────────────────────────────────────────────────────────────────

step "Enrolment"

STATE_FILE="${STATE_DIR}/agent.json"

if [[ -s "$STATE_FILE" ]]; then
  # Kept, always. The token that enrolled this server was one-time and nobody
  # has it any more, so an installer that re-enrolled on every run would turn a
  # routine upgrade into a server nothing can reach.
  success "already enrolled; the existing credential was left alone"
elif [[ -n "$TOKEN" ]]; then
  sudo -u "$SERVICE_USER" "$AGENT" \
    --base-url "$BASE_URL" \
    --state "$STATE_FILE" \
    enrol --token "$TOKEN" \
    || fail "Enrolment was refused. A provisioning token is one-time and short-lived; issue another in KNIGHT."

  success "enrolled against ${BASE_URL}"
else
  warn "no --token given, so this agent is installed and not enrolled"
  warn "issue a provisioning token in KNIGHT (Servers → the server → Add agent) and run:"
  echo "        sudo -u ${SERVICE_USER} ${AGENT} --base-url ${BASE_URL} --state ${STATE_FILE} enrol --token <token>"
fi

# ── The unit ─────────────────────────────────────────────────────────────────

step "Service"

STORE_ARGS=""
for store in "${STORES[@]:-}"; do
  [[ -n "$store" ]] || continue
  [[ -d "$store" ]] || warn "${store} does not exist yet; the agent will report it as missing rather than fail"
  STORE_ARGS+=" --store ${store}"
done

cat > /etc/systemd/system/knight-agent.service <<EOF
[Unit]
Description=KNIGHT agent
Documentation=https://github.com/Parthian-Cataphracts/Knight
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${SERVICE_USER}
Group=${SERVICE_USER}
ExecStart=${AGENT} --base-url ${BASE_URL} --state ${STATE_FILE}${STORE_ARGS} run
Restart=always
RestartSec=30
SyslogIdentifier=knight-agent

# The agent reaches out and listens on nothing, so it needs no inbound rule and
# no privilege it was not started with. The state file is the only thing on this
# machine it may write.
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
ReadWritePaths=${STATE_DIR}

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable knight-agent.service >/dev/null 2>&1

if [[ -s "$STATE_FILE" ]]; then
  systemctl restart knight-agent.service
  success "knight-agent is running"
else
  warn "knight-agent is enabled and not started: it has nothing to authenticate with yet"
  echo "        start it after enrolling:  systemctl start knight-agent"
fi

# ── What an operator needs to know ───────────────────────────────────────────

step "Done"

cat <<SUMMARY

  Agent      ${AGENT}
  Reports to ${BASE_URL}
  State      ${STATE_FILE}  (owner-only; this is the credential)
  Stores     ${STORE_ARGS:-none yet}

  Logs       journalctl -u knight-agent -f
  Status     systemctl status knight-agent

  Revoking the agent in KNIGHT stops it on its next call. There is nothing to
  uninstall here for that — revocation is the control plane's decision and the
  agent honours it without being restarted.

SUMMARY
