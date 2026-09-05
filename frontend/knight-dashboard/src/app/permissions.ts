/**
 * The one place a screen's required permission is declared.
 *
 * Both the sidebar (which hides what you cannot reach) and the router (which
 * refuses a direct link to it) read from here, so the two can never disagree —
 * before this, the nav carried its own copy of each permission and a screen could
 * be hidden from the menu yet reachable by typing its URL.
 *
 * A path absent from this map is reachable by any signed-in operator: the
 * dashboard home and personal settings. These gate the UI only; the API enforces
 * authorization regardless of what the UI shows (docs/authorization.md §6).
 */
export const ROUTE_PERMISSIONS: Record<string, string> = {
  "/customers": "customer.view",
  "/customers/new": "customer.create",
  "/customers/:customerId": "customer.view",
  "/stores": "store.view",
  "/stores/:storeId": "store.view",
  "/provisioning": "store.view",
  "/features": "feature.view",
  "/store-images": "feature.view",
  "/rollouts": "feature.publish",
  "/installations": "installation.view",
  "/plans": "subscription.view",
  "/billing": "billing.view",
  "/infrastructure": "server.view",
  "/monitoring": "monitoring.view",
  "/alerts": "monitoring.view",
  "/errors": "errors.view",
  "/incidents": "incident.view",
  "/logs": "logs.view",
  "/reports": "report.view",
  "/access": "user.view",
  "/audit": "audit.view",
};

/** The permission a route requires, or undefined when any signed-in operator may reach it. */
export function permissionForPath(path: string): string | undefined {
  return ROUTE_PERMISSIONS[path];
}
