import type { DashboardOverview, LoginResponse } from "./types";
import * as fixtures from "./fixtures";
import * as detail from "./fixtures-detail";

/**
 * Development fixtures. The API does not exist yet (TODO.md phase 1), so screens
 * are built against the documented contract and served locally.
 * Active only when VITE_USE_MOCKS=true.
 */

const now = Date.now();
const minutesAgo = (m: number) => new Date(now - m * 60_000).toISOString();

const overview: DashboardOverview = {
  customers: { total: 30, active: 28, suspended: 2, prospect: 0, archived: 0 },
  stores: { total: 34, connected: 30, degraded: 3, disconnected: 1, notRegistered: 0 },
  subscriptions: { total: 30, active: 26, trial: 3, pastDue: 1, suspended: 0, activeEntitlements: 74 },
  billing: { draft: 4, issued: 9, overdue: 1, paid: 51, outstandingTotal: 1840, currency: "EUR" },
  recentActivity: [
    { id: "a1", action: "feature.published", targetType: "Feature", targetId: "f3", actor: "Ali M.", occurredAt: minutesAgo(9) },
    { id: "a2", action: "subscription.plan_changed", targetType: "Subscription", targetId: "sub-1", actor: "Ali M.", occurredAt: minutesAgo(64) },
    { id: "a3", action: "store.created", targetType: "Store", targetId: "s4", actor: "Sara R.", occurredAt: minutesAgo(190) },
    { id: "a4", action: "entitlement.granted", targetType: "FeatureEntitlement", targetId: "e7", actor: "system", occurredAt: minutesAgo(420) },
  ],
};

const session: LoginResponse = {
  status: "succeeded",
  accessToken: "development-token",
  expiresAt: new Date(Date.now() + 900_000).toISOString(),
  refreshToken: "development-refresh-token",
  user: {
    id: "00000000-0000-0000-0000-000000000001",
    email: "admin@knight.local",
    displayName: "مدیر پلتفرم",
    customerId: null,
    roles: ["SuperAdmin"],
    // Every permission a real SuperAdmin holds - ControlPlanePermissions
    // .AssignableToRoles, which is every key except the three only a machine
    // principal may have. Kept complete on purpose: a short list here makes
    // mock mode hide the create and manage actions on every screen, so the
    // screens most worth exercising are the ones it cannot exercise. That is
    // how a Register store button shipped with no handler on it.
    permissions: [
      "customer.view", "customer.create", "customer.update", "customer.archive",
      "store.view", "store.create", "store.manage", "store.credentials.manage",
      "store.provision", "store.deprovision",
      "plan.view", "plan.manage",
      "feature.view", "feature.manage", "feature.publish", "feature.yank",
      "installation.view", "installation.manage", "installation.uninstall", "installation.rollback",
      "job.view", "job.manage",
      "subscription.view", "subscription.manage",
      "billing.view", "billing.manage",
      "server.view", "server.manage", "agent.manage",
      "monitoring.view", "logs.view", "logs.export",
      "errors.view", "errors.manage", "incident.view", "incident.manage",
      "notification.manage",
      "audit.view", "report.view",
      "user.view", "user.manage", "role.view", "role.manage",
    ],
    mfaEnabled: true,
    mfaSatisfied: true,
  },
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function problem(status: number, code: string, detail: string): Response {
  return json({ status, code, detail, title: code, requestId: crypto.randomUUID() }, status);
}

export async function mockFetch(path: string, method: string, body: unknown): Promise<Response> {
  await new Promise((resolve) => setTimeout(resolve, 240));

  if (path === "/auth/login" && method === "POST") {
    const credentials = body as { email?: string; password?: string } | undefined;
    if (!credentials?.email || !credentials.password) {
      return problem(400, "validation_failed", "Email and password are required.");
    }
    if (credentials.password.length < 4) {
      return problem(401, "unauthorized", "Invalid email or password.");
    }
    return json(session);
  }

  // Mocks keep no cookie, so a restore attempt simply says "not signed in".
  if (path === "/auth/refresh") return problem(401, "unauthorized", "No session to restore.");
  if (path === "/auth/me") return json(session.user);
  if (path === "/auth/logout") return new Response(null, { status: 204 });
  if (path === "/monitoring/overview") return json(overview);

  const collections: Record<string, unknown[]> = {
    "/customers": fixtures.customers,
    "/stores": fixtures.stores,
    "/features": fixtures.features,
    "/installations": fixtures.installations,
    "/jobs": fixtures.jobs,
    "/plans": fixtures.plans,
    "/subscriptions": fixtures.subscriptions,
    "/invoices": fixtures.invoices,
    "/servers": fixtures.servers,
    "/errors/groups": fixtures.errorGroups,
    "/incidents": fixtures.incidents,
    "/logs": fixtures.logs,
    "/audit-logs": fixtures.auditEntries,
    "/users": fixtures.admins,
    "/roles": fixtures.roles,
    "/reports": fixtures.reports,
    "/monitoring/alerts": detail.alerts,
    "/notifications": detail.notifications,
  };

  // The notification centre asks with a query string; the fixture set is small
  // enough that the filter would only hide the thing being demonstrated.
  if (path.startsWith("/notifications/channels")) {
    return json({ items: detail.notificationChannels });
  }

  // Not a collection: the fleet overview is one object carrying every server's
  // status and latest load.
  if (path === "/monitoring/fleet") {
    return json(fixtures.fleet);
  }

  // The permission catalogue the role editor offers.
  if (path === "/roles/permissions") {
    const keys = Array.from(new Set(fixtures.roles.flatMap((role) => role.permissions))).sort();
    return json({ items: keys, page: 1, pageSize: keys.length, totalCount: keys.length, totalPages: 1 });
  }

  if (path === "/notifications/rules") {
    return json({
      items: [
        "errors.spike",
        "errors.regression",
        "feature.install.failed",
        "feature.entitled_not_installed",
        "feature.drift",
        "job.stuck",
      ],
    });
  }

  if (path.startsWith("/notifications?")) {
    return json({
      items: detail.notifications,
      page: 1,
      pageSize: detail.notifications.length,
      totalCount: detail.notifications.length,
      totalPages: 1,
    });
  }

  // GET only. Served for any method, a POST to a collection path would come back
  // as a page of items and read as a successful write - so a screen whose save
  // was never wired to anything would look like it worked, which is exactly the
  // defect these fixtures exist to catch. Writes have no fixtures, and the 404
  // below says so where the operator can see it.
  const collection = method === "GET" ? collections[path] : undefined;
  if (collection) {
    return json({
      items: collection,
      page: 1,
      pageSize: collection.length,
      totalCount: collection.length,
      totalPages: 1,
    });
  }

  if (path === "/plans/entitlement-matrix") return json({ items: fixtures.entitlementMatrix });
  if (path === "/infrastructure/services") return json({ items: fixtures.platformServices });

  const scoped: [RegExp, (id: string) => unknown[]][] = [
    [/^\/stores\/([^/]+)\/domains$/, (id) => detail.storeDomains[id] ?? []],
    [/^\/stores\/([^/]+)\/credentials$/, (id) => detail.storeCredentials[id] ?? []],
    [/^\/stores\/([^/]+)\/deployments$/, (id) => detail.storeDeployments[id] ?? []],
    [/^\/stores\/([^/]+)\/activity$/, (id) => detail.storeActivity[id] ?? []],
    [/^\/stores\/([^/]+)\/usage$/, (id) => (detail.storeUsage[id] ? [detail.storeUsage[id]] : [])],
    [/^\/servers\/([^/]+)\/metrics$/, (id) => (detail.serverMetricSeries[id] ? [detail.serverMetricSeries[id]] : [])],
    [/^\/customers\/([^/]+)\/activity$/, (id) => detail.customerActivity[id] ?? []],
    [/^\/customers\/([^/]+)\/notes$/, (id) => detail.customerNotes[id] ?? []],
    [/^\/errors\/groups\/([^/]+)\/events$/, (id) => detail.errorSamples[id] ?? []],
    [/^\/incidents\/([^/]+)\/events$/, (id) => detail.incidentTimeline[id] ?? []],
  ];

  for (const [pattern, resolve] of scoped) {
    const match = pattern.exec(path);
    if (match) {
      const items = resolve(match[1] as string);
      return json({ items, page: 1, pageSize: items.length, totalCount: items.length, totalPages: 1 });
    }
  }

  const installPlan = /^\/stores\/([^/]+)\/features\/([^/]+)\/plan$/.exec(path);
  if (installPlan) {
    return json(installPlan[2] === "f3" ? detail.installPlans["blocked"] : detail.installPlans["ok"]);
  }

  // The job detail endpoint returns the job plus its steps, which the list
  // deliberately does not carry.
  const jobDetail = /^\/jobs\/([^/]+)$/.exec(path);
  if (jobDetail) {
    const job = fixtures.jobs.find((entry) => entry.id === jobDetail[1]);
    return job
      ? json({ job, steps: detail.jobSteps[job.id] ?? [] })
      : problem(404, "not_found", `No job ${jobDetail[1]}`);
  }

  const versions = /^\/features\/([^/]+)\/versions$/.exec(path);
  if (versions) {
    return json({ items: fixtures.featureVersions[versions[1] as string] ?? [] });
  }

  return problem(404, "not_found", `No fixture for ${method} ${path}`);
}
