import { minutesAgo } from "./fixtures";

/** Detail-level fixtures: alerts, per-customer and per-store views, series data. */

const now = Date.now();
const daysAgo = (d: number): string => new Date(now - d * 86_400_000).toISOString();
const daysAhead = (d: number): string => new Date(now + d * 86_400_000).toISOString();

export interface Alert {
  id: string;
  source: string;
  sourceId: string;
  customerId: string | null;
  severity: "Critical" | "Warning" | "Info";
  ruleKey: string;
  message: string;
  raisedAt: string;
  resolvedAt: string | null;
  acknowledgedAt: string | null;
  occurrenceCount: number;
  lastObservedAt: string;
  isOpen: boolean;
}

export const alerts: Alert[] = [
  {
    id: "al1", source: "Server", sourceId: "sv1", customerId: null, severity: "Critical",
    ruleKey: "server.offline", message: "web-01 has not reported for 12 minutes.",
    raisedAt: minutesAgo(12), resolvedAt: null, acknowledgedAt: null,
    occurrenceCount: 12, lastObservedAt: minutesAgo(1), isOpen: true,
  },
  {
    id: "al2", source: "FeatureInstallation", sourceId: "fi1", customerId: "c1", severity: "Warning",
    ruleKey: "feature.entitled_not_installed",
    message: "cafe2.ir is entitled to analytics-reports but it is not installed.",
    raisedAt: minutesAgo(180), resolvedAt: null, acknowledgedAt: minutesAgo(60),
    occurrenceCount: 3, lastObservedAt: minutesAgo(2), isOpen: true,
  },
  {
    id: "al3", source: "Store", sourceId: "s2", customerId: "c2", severity: "Warning",
    ruleKey: "store.unreachable", message: "parsbakery.ir did not answer its health probe.",
    raisedAt: minutesAgo(600), resolvedAt: minutesAgo(540), acknowledgedAt: minutesAgo(580),
    occurrenceCount: 6, lastObservedAt: minutesAgo(541), isOpen: false,
  },
];

export interface StoreDomain {
  id: string;
  host: string;
  type: "Primary" | "Alias" | "Admin" | "Staging";
  /** NotStarted until an operator issues a token, Pending until KNIGHT finds it published. */
  verification: "NotStarted" | "Pending" | "Verified" | "Failed";
  verifiedAt?: string | null;
  verificationMethod?: string | null;
  tlsExpiresAt?: string | null;
}

export interface StoreCredential {
  id: string;
  clientId: string;
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
  /** Evaluated server-side against the current time; the response carries no secret. */
  state: "Active" | "GracePeriod" | "Expired" | "Revoked";
}

export interface Deployment {
  id: string;
  version: string;
  previousVersion?: string | null;
  deployedAt: string;
  /** Null for a deployment KNIGHT learned about from the store rather than from a person. */
  deployedBy?: string | null;
  /** VersionChange when KNIGHT noticed it, StoreReported when the store announced it. */
  source?: "VersionChange" | "StoreReported";
  status: "Detected" | "Succeeded" | "Failed" | "RolledBack";
  notes: string | null;
}

export interface ActivityEntry {
  id: string;
  occurredAt: string;
  kind: "user" | "system" | "warning" | "backup" | "event";
  title: string;
  actor: string;
}

export const storeDomains: Record<string, StoreDomain[]> = {
  s1: [
    { id: "d1", host: "cafe1.ir", type: "Primary", verification: "Verified", tlsExpiresAt: daysAhead(78) },
    { id: "d2", host: "www.cafe1.ir", type: "Alias", verification: "Verified", tlsExpiresAt: daysAhead(78) },
    { id: "d3", host: "admin.cafe1.ir", type: "Admin", verification: "Verified", tlsExpiresAt: daysAhead(3) },
    { id: "d4", host: "new.cafe1.ir", type: "Alias", verification: "Pending", tlsExpiresAt: null },
  ],
  s3: [{ id: "d5", host: "cafe2.ir", type: "Primary", verification: "Verified", tlsExpiresAt: daysAhead(40) }],
};

export const storeCredentials: Record<string, StoreCredential[]> = {
  s1: [
    { id: "cr1", clientId: "kn_cafe1_9f2c41ab", createdAt: daysAgo(120), lastUsedAt: minutesAgo(2), expiresAt: null, state: "Active" },
    { id: "cr2", clientId: "kn_cafe1_1d7790ce", createdAt: daysAgo(400), lastUsedAt: daysAgo(119), expiresAt: daysAgo(113), state: "Revoked" },
  ],
  s3: [{ id: "cr3", clientId: "kn_cafe2_44de7c30", createdAt: daysAgo(60), lastUsedAt: minutesAgo(18), expiresAt: null, state: "Active" }],
};

export const storeDeployments: Record<string, Deployment[]> = {
  s1: [
    { id: "dep1", version: "4.2.0", deployedAt: daysAgo(9), deployedBy: "Ali M.", status: "Succeeded", notes: null },
    { id: "dep2", version: "4.1.3", deployedAt: daysAgo(46), deployedBy: "Ali M.", status: "Succeeded", notes: null },
    { id: "dep3", version: "4.1.2", deployedAt: daysAgo(60), deployedBy: "Sara R.", status: "Failed", notes: "بازگردانی به ۴.۱.۱" },
  ],
};

export const storeActivity: Record<string, ActivityEntry[]> = {
  s1: [
    { id: "sa1", occurredAt: minutesAgo(12), kind: "warning", title: "نصب تحلیل پیشرفته ۱.۴.۰ ناموفق بود", actor: "سامانه" },
    { id: "sa2", occurredAt: minutesAgo(120), kind: "user", title: "چرخش اعتبارنامه فروشگاه", actor: "علی محمدی" },
    { id: "sa3", occurredAt: daysAgo(1), kind: "backup", title: "پشتیبان‌گیری خودکار تکمیل شد", actor: "سامانه" },
    { id: "sa4", occurredAt: daysAgo(9), kind: "system", title: "استقرار نسخه ۴.۲.۰", actor: "خط استقرار" },
  ],
};

/** Requests per hour over the last 24 samples, used by the usage chart. */
export const storeUsage: Record<
  string,
  { errors: number[]; logs: number[]; healthLatencyMs: number[]; windowHours: number; totalErrors: number; totalLogs: number }
> = {
  s1: {
    errors: [1, 0, 0, 0, 0, 1, 2, 3, 2, 4, 6, 12, 18, 22, 14, 9, 7, 5, 4, 3, 2, 1, 1, 0],
    logs: [120, 98, 76, 60, 55, 70, 140, 260, 380, 420, 460, 500, 540, 520, 480, 505, 560, 610, 590, 520, 430, 320, 220, 160],
    healthLatencyMs: [120, 98, 76, 60, 55, 70, 140, 260, 380, 420, 460, 500, 540, 520, 480, 505, 560, 610, 590, 520, 430, 320, 220, 160],
    windowHours: 24,
    totalErrors: 117,
    totalLogs: 8094,
  },
};

export const serverMetricSeries: Record<string, { cpu: number[]; memory: number[] }> = {
  srv1: { cpu: [31, 28, 35, 42, 38, 44, 41, 39, 45, 42, 40, 42], memory: [58, 59, 61, 63, 60, 62, 61, 60, 62, 63, 61, 61] },
  srv2: { cpu: [62, 68, 71, 74, 79, 83, 88, 91, 87, 85, 86, 85], memory: [70, 71, 73, 74, 76, 77, 78, 79, 78, 78, 77, 78] },
  srv3: { cpu: [12, 14, 18, 16, 15, 17, 19, 18, 17, 16, 18, 18], memory: [30, 31, 33, 34, 33, 32, 34, 35, 34, 33, 34, 34] },
  srv4: { cpu: [22, 18, 14, 9, 4, 0, 0, 0, 0, 0, 0, 0], memory: [40, 38, 30, 20, 10, 0, 0, 0, 0, 0, 0, 0] },
};

// --- Customer detail ---------------------------------------------------------

export const customerActivity: Record<string, ActivityEntry[]> = {
  c1: [
    { id: "ca1", occurredAt: minutesAgo(64), kind: "user", title: "ارتقای اشتراک به پلن حرفه‌ای", actor: "علی محمدی" },
    { id: "ca2", occurredAt: minutesAgo(12), kind: "warning", title: "نصب قابلیت ناموفق روی cafe1.ir", actor: "سامانه" },
    { id: "ca3", occurredAt: daysAgo(30), kind: "system", title: "صدور صورتحساب KN-1403-0042", actor: "سامانه" },
    { id: "ca4", occurredAt: daysAgo(120), kind: "user", title: "صدور اعتبارنامه فروشگاه", actor: "سارا رضایی" },
  ],
};

export interface CustomerNote {
  id: string;
  author: string;
  createdAt: string;
  body: string;
}

export const customerNotes: Record<string, CustomerNote[]> = {
  c1: [
    { id: "n1", author: "علی محمدی", createdAt: daysAgo(3), body: "مشتری برای کمپین نوروز درخواست افزایش منابع دیتابیس دارد؛ منتظر تأیید مدیریت." },
    { id: "n2", author: "سامانه", createdAt: daysAgo(30), body: "تمدید سالانه با موفقیت پرداخت شد." },
  ],
};

// --- Error samples and incident timeline -------------------------------------

export interface ErrorEventSample {
  id: string;
  occurredAt: string;
  version: string;
  requestId: string;
  traceId: string;
  stackTrace: string;
}

export const errorSamples: Record<string, ErrorEventSample[]> = {
  eg1: [
    {
      id: "ev1", occurredAt: minutesAgo(4), version: "4.2.0", requestId: "0HMV9A2C41", traceId: "4bf92f3577b34da6",
      stackTrace: [
        'File "apps/orders/views.py", line 142, in create',
        "    order = Order.objects.create(**payload)",
        'File "django/db/models/query.py", line 671, in create',
        "    obj.save(force_insert=True, using=self.db)",
        "IntegrityError: duplicate key value violates unique constraint \"orders_order_reference_key\"",
      ].join("\n"),
    },
    {
      id: "ev2", occurredAt: minutesAgo(38), version: "4.2.0", requestId: "0HMV9A1177", traceId: "9d8c7b6a5f4e3d2c",
      stackTrace: [
        'File "apps/orders/views.py", line 142, in create',
        "    order = Order.objects.create(**payload)",
        "IntegrityError: duplicate key value violates unique constraint \"orders_order_reference_key\"",
      ].join("\n"),
    },
  ],
};

export interface IncidentEvent {
  id: string;
  occurredAt: string;
  type: "Opened" | "Note" | "StatusChanged" | "Mitigated" | "Resolved";
  actor: string;
  message: string;
}

export const incidentTimeline: Record<string, IncidentEvent[]> = {
  in1: [
    { id: "ie1", occurredAt: minutesAgo(12), type: "Opened", actor: "سامانه", message: "قاعده feature.install.failed فعال شد." },
    { id: "ie2", occurredAt: minutesAgo(10), type: "Note", actor: "علی محمدی", message: "مهاجرت ۰۰۰۳ برگشت‌ناپذیر است؛ نیاز به بررسی دستی روی پایگاه داده." },
    { id: "ie3", occurredAt: minutesAgo(6), type: "StatusChanged", actor: "علی محمدی", message: "وضعیت به «در حال بررسی» تغییر کرد." },
  ],
  in2: [
    { id: "ie4", occurredAt: minutesAgo(120), type: "Opened", actor: "سامانه", message: "تأخیر Redis از آستانه عبور کرد." },
    { id: "ie5", occurredAt: minutesAgo(70), type: "Mitigated", actor: "تیم زیرساخت", message: "کلید داغ حذف و اتصال‌ها بازنشانی شد." },
  ],
};

// --- Install preview ---------------------------------------------------------

export interface InstallPlan {
  compatible: boolean;
  verdict: string;
  steps: { slug: string; version: string; role: "dependency" | "target"; alreadyInstalled: boolean }[];
  migrations: { required: boolean; reversible: boolean; estimatedSeconds: number };
  requiresRestart: boolean;
  blockingReason: string | null;
}

export const installPlans: Record<string, InstallPlan> = {
  ok: {
    compatible: true,
    verdict: "سازگار با نسخه فروشگاه ۴.۲.۰",
    steps: [
      { slug: "knight-feature-analytics-core", version: "1.2.3", role: "dependency", alreadyInstalled: true },
      { slug: "knight-feature-analytics", version: "1.4.0", role: "target", alreadyInstalled: false },
    ],
    migrations: { required: true, reversible: true, estimatedSeconds: 30 },
    requiresRestart: true,
    blockingReason: null,
  },
  blocked: {
    compatible: false,
    verdict: "ناسازگار",
    steps: [
      { slug: "knight-feature-analytics", version: "1.4.0", role: "dependency", alreadyInstalled: false },
      { slug: "knight-feature-ai-reports", version: "2.0.1", role: "target", alreadyInstalled: false },
    ],
    migrations: { required: true, reversible: false, estimatedSeconds: 90 },
    requiresRestart: true,
    blockingReason: "این قابلیت زیرساخت اختصاصی می‌خواهد؛ میزبانی این فروشگاه اشتراکی است.",
  },
};

// --- Notifications -----------------------------------------------------------

export interface NotificationDeliveryFixture {
  id: string;
  severity: "Info" | "Warning" | "Critical";
  ruleKey: string;
  title: string;
  body: string;
  status: string;
  createdAt: string;
  readAt: string | null;
}

export const notifications: NotificationDeliveryFixture[] = [
  {
    id: "nd1",
    severity: "Critical",
    ruleKey: "server.offline",
    title: "web-01 has not reported for 5 minutes.",
    body: "Rule server.offline fired on Server web-01.",
    status: "Delivered",
    createdAt: minutesAgo(6),
    readAt: null,
  },
  {
    id: "nd2",
    severity: "Warning",
    ruleKey: "feature.entitled_not_installed",
    title: "cafe2.ir is entitled to analytics-reports but it is not installed.",
    body: "No installation record exists at all.",
    status: "Delivered",
    createdAt: minutesAgo(45),
    readAt: null,
  },
  {
    id: "nd3",
    severity: "Info",
    ruleKey: "server.offline",
    title: "Resolved: web-02 has not reported for 12 minutes.",
    body: "The condition behind server.offline has cleared.",
    status: "Delivered",
    createdAt: minutesAgo(180),
    readAt: minutesAgo(170),
  },
];

export const notificationChannels = [
  {
    id: "nc1",
    customerId: null,
    name: "On-call webhook",
    kind: "Webhook" as const,
    endpoint: "https://hooks.example.com/knight",
    minimumSeverity: "Critical" as const,
    ruleFilter: ["server.offline", "feature.install.failed"],
    isEnabled: true,
    disabledReason: null,
    lastDeliveredAt: minutesAgo(12),
    consecutiveFailures: 0,
    hasSecret: true,
  },
  {
    id: "nc2",
    customerId: null,
    name: "Notification centre",
    kind: "InApp" as const,
    endpoint: null,
    minimumSeverity: "Info" as const,
    ruleFilter: [],
    isEnabled: true,
    disabledReason: null,
    lastDeliveredAt: minutesAgo(4),
    consecutiveFailures: 0,
    hasSecret: false,
  },
];

export const jobSteps: Record<string, {
  sequence: number;
  name: string;
  status: "Running" | "Succeeded" | "Failed" | "Skipped";
  output: string | null;
  errorCode: string | null;
  durationMilliseconds: number | null;
  reportCount: number;
  startedAt: string;
  completedAt: string | null;
}[]> = {
  j2: [
    { sequence: 1, name: "preflight", status: "Succeeded", output: null, errorCode: null, durationMilliseconds: 120, reportCount: 1, startedAt: minutesAgo(15), completedAt: minutesAgo(15) },
    { sequence: 2, name: "fetch", status: "Succeeded", output: null, errorCode: null, durationMilliseconds: 900, reportCount: 1, startedAt: minutesAgo(15), completedAt: minutesAgo(14) },
    { sequence: 3, name: "verify", status: "Succeeded", output: null, errorCode: null, durationMilliseconds: 80, reportCount: 1, startedAt: minutesAgo(14), completedAt: minutesAgo(14) },
    { sequence: 4, name: "install", status: "Succeeded", output: null, errorCode: null, durationMilliseconds: 2200, reportCount: 1, startedAt: minutesAgo(14), completedAt: minutesAgo(13) },
    {
      sequence: 5, name: "migrate", status: "Failed",
      output: 'ProgrammingError: relation "analytics_report" already exists',
      errorCode: "migration_failed", durationMilliseconds: 640, reportCount: 1,
      startedAt: minutesAgo(13), completedAt: minutesAgo(12),
    },
    { sequence: 6, name: "configure", status: "Skipped", output: null, errorCode: null, durationMilliseconds: null, reportCount: 1, startedAt: minutesAgo(12), completedAt: minutesAgo(12) },
  ],
};
