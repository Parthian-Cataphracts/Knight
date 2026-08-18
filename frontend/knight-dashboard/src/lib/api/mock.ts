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
  customers: { active: 28, suspended: 2 },
  stores: { total: 34, connected: 30, degraded: 3, disconnected: 1 },
  alerts: { open: 4, critical: 1 },
  services: [
    { name: "Knight API", state: "Healthy", latencyMs: 14 },
    { name: "PostgreSQL", state: "Healthy", latencyMs: 8 },
    { name: "Redis", state: "Degraded", latencyMs: 145 },
    { name: "Package Registry", state: "Healthy", latencyMs: 32 },
  ],
  resources: { cpuPercent: 42, memoryPercent: 78, diskPercent: 32 },
  featureDelivery: { runningJobs: 3, failedInstallations: 2, entitledNotInstalled: 1 },
  openAlerts: [
    {
      id: "alert-1",
      severity: "critical",
      title: "نصب قابلیت ناموفق بود",
      detail: "cafe1.ir — Advanced Analytics 1.4.0",
      raisedAt: minutesAgo(12),
    },
    {
      id: "alert-2",
      severity: "warning",
      title: "Redis latency",
      detail: "145ms average response time",
      raisedAt: minutesAgo(48),
    },
    {
      id: "alert-3",
      severity: "warning",
      title: "Entitled but not installed",
      detail: "cafe2.ir — AI Reports 2.0.1",
      raisedAt: minutesAgo(180),
    },
  ],
  recentActivity: [
    { id: "a1", action: "feature.installed", target: "cafe3.ir / Advanced Analytics 1.4.0", actor: "system", occurredAt: minutesAgo(9) },
    { id: "a2", action: "subscription.changed", target: "cafe1.ir / Professional", actor: "Ali M.", occurredAt: minutesAgo(64) },
    { id: "a3", action: "store.registered", target: "cafe4.ir", actor: "Sara R.", occurredAt: minutesAgo(190) },
    { id: "a4", action: "feature.published", target: "AI Reports 2.0.1", actor: "Ali M.", occurredAt: minutesAgo(420) },
  ],
};

const session: LoginResponse = {
  accessToken: "development-token",
  expiresIn: 900,
  user: {
    id: "00000000-0000-0000-0000-000000000001",
    email: "admin@knight.local",
    displayName: "مدیر پلتفرم",
    customerId: null,
    roles: ["SuperAdmin"],
    permissions: [
      "customer.view",
      "store.view",
      "store.manage",
      "feature.view",
      "feature.manage",
      "feature.publish",
      "installation.view",
      "installation.manage",
      "job.view",
      "subscription.view",
      "billing.view",
      "server.view",
      "monitoring.view",
      "errors.view",
      "incident.view",
      "logs.view",
      "audit.view",
      "user.view",
      "report.view",
    ],
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
    "/alerts": detail.alerts,
  };

  const collection = collections[path];
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

  const versions = /^\/features\/([^/]+)\/versions$/.exec(path);
  if (versions) {
    return json({ items: fixtures.featureVersions[versions[1] as string] ?? [] });
  }

  return problem(404, "not_found", `No fixture for ${method} ${path}`);
}
