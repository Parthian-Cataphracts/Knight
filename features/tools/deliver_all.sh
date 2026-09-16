#!/usr/bin/env bash
#
# Scaffold and deliver every deliverable optional Feature to the store, in one
# run. Each line is `slug|Display Name|port`; base-store capabilities
# (catalog, orders, payments, storefront, shipping, accounts, coupons) are not
# here — they are the store itself, not delivered Features.
#
# Reads the same credentials deliver_service.sh does (KNIGHT_ADMIN_EMAIL,
# KNIGHT_ADMIN_PASSWORD, STORE_ID, optionally SSH_KEY/KNIGHT_SSH/STORE_SSH) from
# the environment. Idempotent enough to re-run: a control secret is reused, a
# cert left alone, and a re-publish/re-install is harmless.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
: "${STORE_ID:?}"; : "${KNIGHT_ADMIN_EMAIL:?}"; : "${KNIGHT_ADMIN_PASSWORD:?}"

FEATURES=(
  "advanced-inventory|Advanced Inventory|8092"
  "advanced-search|Advanced Search|8093"
  "advanced-promotions|Advanced Promotions|8094"
  "analytics-reports|Analytics Reports|8095"
  "customer-segmentation|Customer Segmentation|8096"
  "gift-cards|Gift Cards and Store Credit|8097"
  "marketing-automation|Marketing Automation|8098"
  "reviews-ratings|Reviews and Ratings|8099"
)

ok=(); failed=()
for entry in "${FEATURES[@]}"; do
  IFS='|' read -r slug name port <<<"$entry"
  dir="$root/features/knight-feature-${slug}-service"
  echo; echo "###################### $slug ######################"
  if [[ ! -d "$dir" ]]; then
    python "$here/scaffold_external_service.py" --slug "$slug" --name "$name" \
      --subdomain "${slug}.bojanstore.com" --port "$port" || { failed+=("$slug"); continue; }
  fi
  if FEATURE_DIR="$dir" SLUG="$slug" SUBDOMAIN="${slug}.bojanstore.com" PORT="$port" \
     STORE_ID="$STORE_ID" KNIGHT_ADMIN_EMAIL="$KNIGHT_ADMIN_EMAIL" KNIGHT_ADMIN_PASSWORD="$KNIGHT_ADMIN_PASSWORD" \
     bash "$here/deliver_service.sh"; then
    ok+=("$slug")
  else
    failed+=("$slug")
  fi
done

echo; echo "====================== SUMMARY ======================"
echo "delivered: ${ok[*]:-none}"
echo "failed:    ${failed[*]:-none}"
