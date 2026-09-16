#!/usr/bin/env bash
#
# Publish, deploy and install a scaffolded external-service Feature end to end.
#
# One command does what connecting analytics took by hand: sign and publish the
# version to KNIGHT with the prod key, put the control secret on both sides,
# build and run the service container behind nginx with a TLS cert, install the
# Feature onto the store and issue the store its service secret — then verify.
#
# It is idempotent where it can be: an existing control secret is reused, an
# existing cert is left alone, and a re-run re-publishes and re-installs cleanly.
#
# Prerequisites you provide (env or a .env beside this script):
#   FEATURE_DIR   local path to features/knight-feature-<slug>-service
#   SLUG          catalogue slug the store is entitled to
#   SUBDOMAIN     public host for the service (DNS A record must already point at STORE_HOST)
#   PORT          container port
#   STORE_ID      the store's KNIGHT id
#   KNIGHT_ADMIN_EMAIL / KNIGHT_ADMIN_PASSWORD   a platform admin (for the API token)
# Optional:
#   VERSION (default 2.0.0), SSH_KEY (~/.ssh/knight_bojano),
#   KNIGHT_SSH (root@167.233.37.47), STORE_SSH (root@91.107.159.162),
#   KNIGHT_API (http://127.0.0.1:5080), CERT_EMAIL (admin@<subdomain-root>)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[[ -f "$here/.env" ]] && set -a && . "$here/.env" && set +a

: "${FEATURE_DIR:?}"; : "${SLUG:?}"; : "${SUBDOMAIN:?}"; : "${PORT:?}"; : "${STORE_ID:?}"
: "${KNIGHT_ADMIN_EMAIL:?}"; : "${KNIGHT_ADMIN_PASSWORD:?}"
VERSION="${VERSION:-2.0.0}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/knight_bojano}"
KNIGHT_SSH="${KNIGHT_SSH:-root@167.233.37.47}"
STORE_SSH="${STORE_SSH:-root@91.107.159.162}"
KNIGHT_API="${KNIGHT_API:-http://127.0.0.1:5080}"
CERT_EMAIL="${CERT_EMAIL:-admin@${SUBDOMAIN#*.}}"
PREFIX="$(printf '%s' "$SLUG" | tr '[:lower:]-' '[:upper:]_' | tr -cd 'A-Z0-9_')"
CONTAINER="bojan-${SLUG}"
SSH="ssh -i $SSH_KEY -o StrictHostKeyChecking=accept-new"
SCP="scp -i $SSH_KEY -o StrictHostKeyChecking=accept-new"

say() { printf '\n\033[1;36m━━ %s\033[0m\n' "$*"; }

token() {
  $SSH "$KNIGHT_SSH" "curl -sS -m 10 -X POST $KNIGHT_API/api/v1/auth/login \
    -H 'Content-Type: application/json' \
    -d '{\"email\":\"$KNIGHT_ADMIN_EMAIL\",\"password\":\"$KNIGHT_ADMIN_PASSWORD\"}' \
    | python3 -c 'import sys,json;print(json.load(sys.stdin)[\"accessToken\"])'"
}

say "1/8  Publish $SLUG $VERSION to KNIGHT (signed with the prod key)"
$SSH "$KNIGHT_SSH" "rm -rf /tmp/deliver-$SLUG && mkdir -p /tmp/deliver-$SLUG"
$SCP -r "$FEATURE_DIR/." "$KNIGHT_SSH:/tmp/deliver-$SLUG/" >/dev/null
$SSH "$KNIGHT_SSH" "chown -R knight:knight /tmp/deliver-$SLUG"
TOKEN="$(token)"
$SSH "$KNIGHT_SSH" "PRIV=\$(sudo -u knight sed -nE \"s/^FeatureArtifacts__Keys__prod__PrivateKey='([^']+)'.*/\\1/p\" /opt/knight/knight.env); \
  sudo -u knight env KNIGHT_SIGNING_KEY=\"\$PRIV\" KNIGHT_SIGNING_KEY_ID=prod KNIGHT_TOKEN='$TOKEN' \
    KNIGHT_BASE_URL=$KNIGHT_API KNIGHT_ARTIFACT_ROOT=/opt/knight/artifacts \
    python3 /opt/knight/src/features/tools/knight_package.py publish /tmp/deliver-$SLUG 2>&1 | tail -4"

say "2/8  Control secret for $SLUG on KNIGHT (reuse if present)"
CONTROL_SECRET="$($SSH "$KNIGHT_SSH" "sudo -u knight python3 - <<PY
import json,os,secrets,base64
p='/opt/knight/app/api/appsettings.Production.json'
d=json.load(open(p)) if os.path.exists(p) else {}
node=d.setdefault('ServiceControlPlane',{}).setdefault('Secrets',{})
if not node.get('$SLUG'):
    node['$SLUG']=base64.b64encode(secrets.token_bytes(32)).decode()
    json.dump(d,open(p,'w'),indent=2); os.chmod(p,0o600)
print(node['$SLUG'])
PY")"
$SSH "$KNIGHT_SSH" "systemctl restart knight-api; sleep 7; curl -sS -m 8 -o /dev/null -w 'knight ready:%{http_code}\n' $KNIGHT_API/health/ready"

say "3/8  Deploy the service container on the store host"
$SSH "$STORE_SSH" "mkdir -p /opt/$SLUG-service"
$SCP -r "$FEATURE_DIR/service" "$FEATURE_DIR/deploy" "$STORE_SSH:/opt/$SLUG-service/" >/dev/null
$SSH "$STORE_SSH" "cd /opt/$SLUG-service/deploy; umask 077; printf '%s_CONTROL_SECRET=%s\n' '$PREFIX' '$CONTROL_SECRET' > .env; \
  docker compose up -d --build 2>&1 | tail -3; sleep 4; curl -sS -m 8 -o /dev/null -w 'service healthz:%{http_code}\n' http://127.0.0.1:$PORT/healthz"

say "4/8  nginx vhost + TLS for $SUBDOMAIN"
$SSH "$STORE_SSH" "bash -s" <<NGINX
set -e
cat > /etc/nginx/sites-available/$SLUG <<CONF
server {
    listen 80;
    listen [::]:80;
    server_name $SUBDOMAIN;
    location / {
        proxy_pass http://127.0.0.1:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \\\$host;
        proxy_set_header X-Real-IP \\\$remote_addr;
        proxy_set_header X-Forwarded-For \\\$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \\\$scheme;
    }
}
CONF
ln -sf /etc/nginx/sites-available/$SLUG /etc/nginx/sites-enabled/$SLUG
nginx -t >/dev/null 2>&1 && systemctl reload nginx
if [ ! -d /etc/letsencrypt/live/$SUBDOMAIN ]; then
  certbot --nginx -d $SUBDOMAIN --non-interactive --agree-tos --email $CERT_EMAIL --redirect >/dev/null 2>&1 \
    && echo "cert issued" || echo "cert FAILED (does $SUBDOMAIN resolve to this host yet?)"
else
  echo "cert already present"
fi
curl -sS -m 10 -o /dev/null -w "https healthz:%{http_code}\n" https://$SUBDOMAIN/healthz || true
NGINX

say "5/8  Install $SLUG onto the store"
TOKEN="$(token)"
$SSH "$KNIGHT_SSH" "curl -sS -m 25 -X POST $KNIGHT_API/api/v1/installations/install \
  -H 'Authorization: Bearer $TOKEN' -H 'Content-Type: application/json' \
  -d '{\"storeId\":\"$STORE_ID\",\"slug\":\"$SLUG\",\"versionRange\":\">=$VERSION\"}' \
  | python3 -c 'import sys,json;d=json.load(sys.stdin);p=d[\"plan\"];print(\"plan ok:\",p[\"isSuccessful\"],\"failures:\",p.get(\"failures\"))'"

say "6/8  Wait for the agent to install it"
for i in $(seq 1 20); do
  state="$($SSH "$KNIGHT_SSH" "export PGPASSWORD=\$(sudo -u knight grep ControlPlane /opt/knight/knight.env | grep -oE \"Password=[^;']+\" | head -1 | cut -d= -f2-); \
    psql -h 127.0.0.1 -U knight -d knight -tA -w -c \"SELECT i.\\\"State\\\" FROM control.feature_installations i JOIN control.features f ON f.\\\"Id\\\"=i.\\\"FeatureId\\\" WHERE i.\\\"StoreId\\\"='$STORE_ID' AND f.\\\"Slug\\\"='$SLUG';\"")"
  echo "  installation: ${state:-<none>}"
  [[ "$state" == "Installed" ]] && break
  sleep 15
done

say "7/8  Issue the store's service secret"
TOKEN="$(token)"
$SSH "$KNIGHT_SSH" "FID=\$(export PGPASSWORD=\$(sudo -u knight grep ControlPlane /opt/knight/knight.env | grep -oE \"Password=[^;']+\" | head -1 | cut -d= -f2-); psql -h 127.0.0.1 -U knight -d knight -tA -w -c \"SELECT \\\"Id\\\" FROM control.features WHERE \\\"Slug\\\"='$SLUG';\"); \
  curl -sS -m 20 -X POST $KNIGHT_API/api/v1/installations/service-secret -H 'Authorization: Bearer $TOKEN' -H 'Content-Type: application/json' \
    -d \"{\\\"storeId\\\":\\\"$STORE_ID\\\",\\\"featureId\\\":\\\"\$FID\\\"}\" \
    | python3 -c 'import sys,json;d=json.load(sys.stdin);print(\"service-secret:\",d.get(\"secretName\") or d)'"

say "8/8  Done"
echo "  $SLUG $VERSION is published, running at https://$SUBDOMAIN, installed on store $STORE_ID."
echo "  If installation did not reach 'Installed', check: knightctl logs api  and the store's container logs."
