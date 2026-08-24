import type {
  AdminUser,
  AuditEntry,
  Customer,
  EntitlementMatrixRow,
  ErrorGroup,
  Feature,
  FeatureVersion,
  FleetOverview,
  Incident,
  Installation,
  Invoice,
  Job,
  LogEntry,
  Plan,
  Role,
  Server,
  Store,
  Subscription,
} from "./domain";

/** Development fixtures shaped exactly like the documented API responses. */

const now = Date.now();
export const minutesAgo = (m: number): string => new Date(now - m * 60_000).toISOString();
const daysAgo = (d: number): string => new Date(now - d * 86_400_000).toISOString();
const daysAhead = (d: number): string => new Date(now + d * 86_400_000).toISOString();

export const customers: Customer[] = [
  { id: "c1", name: "کافه وان", legalName: "شرکت کافه وان", phone: "+982188001122", notes: "قرارداد سالانه، تمدید خودکار.", contactEmail: "owner@cafe1.ir", status: "Active", planKey: "professional", storeCount: 2, dataRetentionOverrideDays: null, createdAt: daysAgo(420) },
  { id: "c2", name: "کافه تو", legalName: null, phone: "+982177445566", notes: null, contactEmail: "info@cafe2.ir", status: "Active", planKey: "custom", storeCount: 1, dataRetentionOverrideDays: null, createdAt: daysAgo(300) },
  { id: "c3", name: "شیرینی سرای پارس", legalName: "شیرینی سرای پارس", phone: null, notes: null, contactEmail: "hello@parsbakery.ir", status: "Active", planKey: "basic", storeCount: 1, dataRetentionOverrideDays: null, createdAt: daysAgo(180) },
  { id: "c4", name: "رستوران البرز", legalName: null, phone: null, notes: "تعلیق به دلیل صورتحساب معوق.", contactEmail: "admin@alborz.ir", status: "Suspended", planKey: "basic", storeCount: 1, dataRetentionOverrideDays: null, createdAt: daysAgo(150) },
  { id: "c5", name: "قنادی نوین", legalName: null, phone: null, notes: null, contactEmail: "sales@novin.ir", status: "Prospect", planKey: "basic", storeCount: 0, dataRetentionOverrideDays: null, createdAt: daysAgo(12) },
];

export const stores: Store[] = [
  { id: "s1", customerId: "c1", customerName: "کافه وان", name: "فروشگاه اصلی", primaryDomain: "cafe1.ir", environment: "Production", applicationVersion: "4.2.0", integrationStatus: "Connected", hostingModel: "DedicatedManaged", status: "Active", installedFeatureCount: 5, serverId: "srv2", requiresMutualTls: false, mutualTlsThumbprint: null, lastSeenAt: minutesAgo(2) },
  { id: "s2", customerId: "c1", customerName: "کافه وان", name: "محیط آزمایشی", primaryDomain: "staging.cafe1.ir", environment: "Staging", applicationVersion: "4.3.0-rc1", integrationStatus: "Connected", hostingModel: "SharedManaged", status: "Active", installedFeatureCount: 6, serverId: "srv3", requiresMutualTls: false, mutualTlsThumbprint: null, lastSeenAt: minutesAgo(5) },
  { id: "s3", customerId: "c2", customerName: "کافه تو", name: "فروشگاه اصلی", primaryDomain: "cafe2.ir", environment: "Production", applicationVersion: "4.1.3", integrationStatus: "Degraded", hostingModel: "SharedManaged", status: "Active", installedFeatureCount: 3, serverId: "srv1", requiresMutualTls: false, mutualTlsThumbprint: null, lastSeenAt: minutesAgo(18) },
  { id: "s4", customerId: "c3", customerName: "شیرینی سرای پارس", name: "فروشگاه اصلی", primaryDomain: "parsbakery.ir", environment: "Production", applicationVersion: "4.2.0", integrationStatus: "Connected", hostingModel: "SharedManaged", status: "Active", installedFeatureCount: 2, serverId: "srv1", requiresMutualTls: false, mutualTlsThumbprint: null, lastSeenAt: minutesAgo(1) },
  { id: "s5", customerId: "c4", customerName: "رستوران البرز", name: "فروشگاه اصلی", primaryDomain: "alborz.ir", environment: "Production", applicationVersion: "3.9.2", integrationStatus: "Disconnected", hostingModel: "SharedManaged", status: "Suspended", installedFeatureCount: 2, serverId: "srv4", requiresMutualTls: false, mutualTlsThumbprint: null, lastSeenAt: daysAgo(6) },
  { id: "s6", customerId: "c5", customerName: "قنادی نوین", name: "فروشگاه جدید", primaryDomain: "novin.ir", environment: "Production", applicationVersion: null, integrationStatus: "Pending", hostingModel: "SharedManaged", status: "Provisioning", installedFeatureCount: 0, serverId: null, requiresMutualTls: false, mutualTlsThumbprint: null, lastSeenAt: null },
];

export const features: Feature[] = [
  { id: "f1", slug: "knight-feature-analytics-core", name: "هسته تحلیل داده", description: "زیرساخت جمع‌آوری و تجمیع رویدادهای فروشگاه.", category: "analytics", status: "Published", isOptional: false, requiresDedicatedInfrastructure: false, latestVersion: "1.2.3", installCount: 5, entitledCount: 5, plans: ["basic", "custom", "professional"] },
  { id: "f2", slug: "knight-feature-analytics", name: "تحلیل پیشرفته", description: "داشبورد فروش، قیف تبدیل و گزارش‌های دوره‌ای.", category: "analytics", status: "Published", isOptional: true, requiresDedicatedInfrastructure: false, latestVersion: "1.4.0", installCount: 3, entitledCount: 4, plans: ["custom", "professional"] },
  { id: "f3", slug: "knight-feature-ai-reports", name: "گزارش‌های هوشمند", description: "خلاصه‌سازی و تحلیل خودکار گزارش‌ها.", category: "ai", status: "Published", isOptional: true, requiresDedicatedInfrastructure: true, latestVersion: "2.0.1", installCount: 1, entitledCount: 2, plans: ["professional"] },
  { id: "f4", slug: "knight-feature-sms", name: "اعلان پیامکی", description: "ارسال پیامک وضعیت سفارش به مشتری فروشگاه.", category: "notifications", status: "Published", isOptional: true, requiresDedicatedInfrastructure: false, latestVersion: "1.1.0", installCount: 2, entitledCount: 2, plans: ["custom", "professional"] },
  { id: "f5", slug: "knight-feature-loyalty", name: "باشگاه مشتریان", description: "امتیازدهی، سطوح کاربری و تخفیف وفاداری.", category: "crm", status: "Draft", isOptional: true, requiresDedicatedInfrastructure: false, latestVersion: "0.3.0", installCount: 0, entitledCount: 0, plans: [] },
  { id: "f6", slug: "knight-feature-webhooks", name: "وب‌هوک سفارشی", description: "ارسال رویدادهای فروشگاه به سرویس‌های بیرونی.", category: "integration", status: "Deprecated", isOptional: true, requiresDedicatedInfrastructure: false, latestVersion: "1.0.4", installCount: 1, entitledCount: 1, plans: ["professional"] },
];

export const featureVersions: Record<string, FeatureVersion[]> = {
  f2: [
    { id: "v-f2-140", featureId: "f2", version: "1.4.0", status: "Published", packageReference: "knight-feature-analytics==1.4.0", artifactDigest: "sha256:9f2c…41ab", signed: true, storeVersionRange: ">=4.0.0,<6.0.0", dependencies: [{ slug: "knight-feature-analytics-core", range: ">=1.2.0,<2.0.0" }], migrations: { required: true, reversible: true, estimatedSeconds: 30 }, publishedAt: daysAgo(9), publishedBy: "Ali M." },
    { id: "v-f2-131", featureId: "f2", version: "1.3.1", status: "Published", packageReference: "knight-feature-analytics==1.3.1", artifactDigest: "sha256:1d77…90ce", signed: true, storeVersionRange: ">=4.0.0,<5.0.0", dependencies: [{ slug: "knight-feature-analytics-core", range: ">=1.1.0,<2.0.0" }], migrations: { required: true, reversible: true, estimatedSeconds: 25 }, publishedAt: daysAgo(46), publishedBy: "Ali M." },
    { id: "v-f2-130", featureId: "f2", version: "1.3.0", status: "Yanked", packageReference: "knight-feature-analytics==1.3.0", artifactDigest: "sha256:b0aa…22f1", signed: true, storeVersionRange: ">=4.0.0,<5.0.0", dependencies: [], migrations: { required: true, reversible: false, estimatedSeconds: 40 }, publishedAt: daysAgo(60), publishedBy: "Ali M." },
  ],
  f3: [
    { id: "v-f3-201", featureId: "f3", version: "2.0.1", status: "Published", packageReference: "knight-feature-ai-reports==2.0.1", artifactDigest: "sha256:44de…7c30", signed: true, storeVersionRange: ">=4.2.0,<6.0.0", dependencies: [{ slug: "knight-feature-analytics", range: ">=1.3.0,<2.0.0" }], migrations: { required: true, reversible: false, estimatedSeconds: 90 }, publishedAt: daysAgo(7), publishedBy: "Ali M." },
  ],
};

export const installations: Installation[] = [
  {
    id: "i1", storeId: "s1", storeName: "cafe1.ir", featureId: "f2",
    featureName: "\u062a\u062d\u0644\u06cc\u0644 \u067e\u06cc\u0634\u0631\u0641\u062a\u0647",
    featureSlug: "knight-feature-analytics", entitled: true, isEnabled: false,
    installedVersion: null, targetVersion: "1.4.0", previousVersion: null,
    state: "Failed", health: "Offline", currentJobId: "j2",
    failureCode: "migration_failed", failureMessage: "ProgrammingError in migration 0003",
    rollbackOutcome: "ManualInterventionRequired",
    blockingReason: "ProgrammingError in migration 0003",
    requiresManualIntervention: true, installedAt: null, disabledAt: null,
    lastTransitionAt: minutesAgo(12),
  },
  {
    id: "i2", storeId: "s1", storeName: "cafe1.ir", featureId: "f1",
    featureName: "\u0647\u0633\u062a\u0647 \u062a\u062d\u0644\u06cc\u0644 \u062f\u0627\u062f\u0647",
    featureSlug: "knight-feature-analytics-core", entitled: true, isEnabled: true,
    installedVersion: "1.2.3", targetVersion: "1.2.3", previousVersion: null,
    state: "Installed", health: "Healthy", currentJobId: null,
    failureCode: null, failureMessage: null, rollbackOutcome: "NotAttempted",
    blockingReason: null, requiresManualIntervention: false,
    installedAt: daysAgo(9), disabledAt: null, lastTransitionAt: daysAgo(9),
  },
  {
    id: "i3", storeId: "s2", storeName: "staging.cafe1.ir", featureId: "f2",
    featureName: "\u062a\u062d\u0644\u06cc\u0644 \u067e\u06cc\u0634\u0631\u0641\u062a\u0647",
    featureSlug: "knight-feature-analytics", entitled: true, isEnabled: true,
    installedVersion: "1.3.1", targetVersion: "1.4.0", previousVersion: "1.3.0",
    state: "Updating", health: "Healthy", currentJobId: "j1",
    failureCode: null, failureMessage: null, rollbackOutcome: "NotAttempted",
    blockingReason: null, requiresManualIntervention: false,
    installedAt: daysAgo(20), disabledAt: null, lastTransitionAt: minutesAgo(3),
  },
  {
    id: "i4", storeId: "s3", storeName: "cafe2.ir", featureId: "f3",
    featureName: "\u06af\u0632\u0627\u0631\u0634\u200c\u0647\u0627\u06cc \u0647\u0648\u0634\u0645\u0646\u062f",
    featureSlug: "knight-feature-ai-reports", entitled: true, isEnabled: false,
    installedVersion: null, targetVersion: null, previousVersion: null,
    state: "NotInstalled", health: "Unknown", currentJobId: null,
    failureCode: null, failureMessage: null, rollbackOutcome: "NotAttempted",
    blockingReason: "Requires dedicated infrastructure; this store is on shared hosting.",
    requiresManualIntervention: false, installedAt: null, disabledAt: null,
    lastTransitionAt: daysAgo(3),
  },
  {
    id: "i5", storeId: "s3", storeName: "cafe2.ir", featureId: "f4",
    featureName: "\u0627\u0639\u0644\u0627\u0646 \u067e\u06cc\u0627\u0645\u06a9\u06cc",
    featureSlug: "knight-feature-sms", entitled: true, isEnabled: true,
    installedVersion: "1.1.0", targetVersion: "1.1.0", previousVersion: "1.0.2",
    state: "Installed", health: "Healthy", currentJobId: null,
    failureCode: null, failureMessage: null, rollbackOutcome: "NotAttempted",
    blockingReason: null, requiresManualIntervention: false,
    installedAt: daysAgo(21), disabledAt: null, lastTransitionAt: daysAgo(21),
  },
  {
    id: "i6", storeId: "s5", storeName: "alborz.ir", featureId: "f4",
    featureName: "\u0627\u0639\u0644\u0627\u0646 \u067e\u06cc\u0627\u0645\u06a9\u06cc",
    featureSlug: "knight-feature-sms", entitled: false, isEnabled: false,
    installedVersion: "1.0.2", targetVersion: null, previousVersion: null,
    state: "Disabled", health: "Unknown", currentJobId: null,
    failureCode: null, failureMessage: null, rollbackOutcome: "NotAttempted",
    blockingReason: "Entitlement lapsed; data is retained for 30 days.",
    requiresManualIntervention: false, installedAt: daysAgo(60), disabledAt: daysAgo(6),
    lastTransitionAt: daysAgo(6),
  },
  {
    id: "i7", storeId: "s4", storeName: "parsbakery.ir", featureId: "f1",
    featureName: "\u0647\u0633\u062a\u0647 \u062a\u062d\u0644\u06cc\u0644 \u062f\u0627\u062f\u0647",
    featureSlug: "knight-feature-analytics-core", entitled: true, isEnabled: true,
    installedVersion: "1.2.3", targetVersion: "1.2.3", previousVersion: null,
    state: "Installed", health: "Healthy", currentJobId: null,
    failureCode: null, failureMessage: null, rollbackOutcome: "NotAttempted",
    blockingReason: null, requiresManualIntervention: false,
    installedAt: daysAgo(30), disabledAt: null, lastTransitionAt: daysAgo(30),
  },
];

export const jobs: Job[] = [
  {
    id: "j1", storeId: "s2", storeName: "staging.cafe1.ir", featureId: "f2",
    featureSlug: "knight-feature-analytics", type: "Upgrade", state: "Running",
    targetVersion: "1.4.0", trigger: "Manual",
    completedStepCount: 6, totalStepCount: 9, attemptCount: 1, maxAttempts: 3,
    failureCode: null, failureMessage: null, rollbackOutcome: "NotAttempted",
    queuedAt: minutesAgo(4), claimedAt: minutesAgo(3), completedAt: null,
    correlationId: "0HMV9A2C41",
  },
  {
    id: "j2", storeId: "s1", storeName: "cafe1.ir", featureId: "f2",
    featureSlug: "knight-feature-analytics", type: "Install", state: "Failed",
    targetVersion: "1.4.0", trigger: "Manual",
    completedStepCount: 5, totalStepCount: 9, attemptCount: 3, maxAttempts: 3,
    failureCode: "migration_failed",
    failureMessage: 'ProgrammingError: relation "analytics_report" already exists',
    rollbackOutcome: "ManualInterventionRequired",
    queuedAt: minutesAgo(16), claimedAt: minutesAgo(15), completedAt: minutesAgo(12),
    correlationId: "0HMV9A1B77",
  },
  {
    id: "j3", storeId: "s6", storeName: "novin.ir", featureId: "f1",
    featureSlug: "knight-feature-analytics-core", type: "Install", state: "Queued",
    targetVersion: "1.2.3", trigger: "Provisioning",
    completedStepCount: 0, totalStepCount: 9, attemptCount: 0, maxAttempts: 3,
    failureCode: null, failureMessage: null, rollbackOutcome: "NotAttempted",
    queuedAt: minutesAgo(6), claimedAt: null, completedAt: null,
    correlationId: "0HMV9A3D02",
  },
  {
    id: "j4", storeId: "s4", storeName: "parsbakery.ir", featureId: "f1",
    featureSlug: "knight-feature-analytics-core", type: "HealthCheck", state: "Succeeded",
    targetVersion: null, trigger: "Schedule",
    completedStepCount: 1, totalStepCount: 1, attemptCount: 1, maxAttempts: 3,
    failureCode: null, failureMessage: null, rollbackOutcome: "NotAttempted",
    queuedAt: daysAgo(1), claimedAt: daysAgo(1), completedAt: daysAgo(1),
    correlationId: "0HMV98ZZ10",
  },
  {
    id: "j5", storeId: "s3", storeName: "cafe2.ir", featureId: "f4",
    featureSlug: "knight-feature-sms", type: "Rollback", state: "Succeeded",
    targetVersion: "1.0.2", trigger: "Manual",
    completedStepCount: 5, totalStepCount: 5, attemptCount: 1, maxAttempts: 3,
    failureCode: null, failureMessage: null, rollbackOutcome: "RolledBack",
    queuedAt: daysAgo(2), claimedAt: daysAgo(2), completedAt: daysAgo(2),
    correlationId: "0HMV97YY31",
  },
];

export const plans: Plan[] = [
  { id: "p1", key: "basic", name: "پایه", description: "مجموعه‌ی پایه و اجباری برای هر فروشگاه.", basePrice: 0, currency: "IRT", isActive: true, sortOrder: 0, customerCount: 2, includedFeatures: ["knight-feature-analytics-core"], optionalFeatures: [] },
  { id: "p2", key: "custom", name: "سفارشی", description: "پایه به‌علاوه‌ی قابلیت‌های انتخابی مشتری.", basePrice: 2_990_000, currency: "IRT", isActive: true, sortOrder: 1, customerCount: 1, includedFeatures: ["knight-feature-analytics-core"], optionalFeatures: ["knight-feature-analytics", "knight-feature-sms"] },
  { id: "p3", key: "professional", name: "حرفه‌ای", description: "زیرساخت اختصاصی به‌همراه تمام قابلیت‌ها.", basePrice: 8_900_000, currency: "IRT", isActive: true, sortOrder: 2, customerCount: 1, includedFeatures: ["knight-feature-analytics-core", "knight-feature-analytics"], optionalFeatures: ["knight-feature-ai-reports", "knight-feature-sms", "knight-feature-webhooks"] },
];

export const entitlementMatrix: EntitlementMatrixRow[] = [
  { featureSlug: "knight-feature-analytics-core", featureName: "هسته تحلیل داده", values: { basic: true, custom: true, professional: true } },
  { featureSlug: "knight-feature-analytics", featureName: "تحلیل پیشرفته", values: { basic: false, custom: "اختیاری", professional: true } },
  { featureSlug: "knight-feature-ai-reports", featureName: "گزارش‌های هوشمند", values: { basic: false, custom: false, professional: "اختیاری" } },
  { featureSlug: "knight-feature-sms", featureName: "اعلان پیامکی", values: { basic: false, custom: "اختیاری", professional: "اختیاری" } },
  { featureSlug: "knight-feature-webhooks", featureName: "وب‌هوک سفارشی", values: { basic: false, custom: false, professional: "اختیاری" } },
];

export const subscriptions: Subscription[] = [
  { id: "sub1", customerId: "c1", customerName: "کافه وان", planKey: "professional", planName: "حرفه‌ای", status: "Active", optionalFeatures: 2, monthlyTotal: 11_400_000, currency: "IRT", currentPeriodEnd: daysAhead(18) },
  { id: "sub2", customerId: "c2", customerName: "کافه تو", planKey: "custom", planName: "سفارشی", status: "Active", optionalFeatures: 1, monthlyTotal: 3_890_000, currency: "IRT", currentPeriodEnd: daysAhead(4) },
  { id: "sub3", customerId: "c3", customerName: "شیرینی سرای پارس", planKey: "basic", planName: "پایه", status: "Active", optionalFeatures: 0, monthlyTotal: 0, currency: "IRT", currentPeriodEnd: daysAhead(25) },
  { id: "sub4", customerId: "c4", customerName: "رستوران البرز", planKey: "basic", planName: "پایه", status: "PastDue", optionalFeatures: 0, monthlyTotal: 0, currency: "IRT", currentPeriodEnd: daysAgo(6) },
];

export const invoices: Invoice[] = [
  { id: "inv1", number: "KN-1403-0042", customerName: "کافه وان", periodStart: daysAgo(30), periodEnd: daysAgo(1), total: 11_400_000, currency: "IRT", status: "Paid", issuedAt: daysAgo(30) },
  { id: "inv2", number: "KN-1403-0043", customerName: "کافه تو", periodStart: daysAgo(30), periodEnd: daysAgo(1), total: 3_890_000, currency: "IRT", status: "Issued", issuedAt: daysAgo(3) },
  { id: "inv3", number: "KN-1403-0044", customerName: "رستوران البرز", periodStart: daysAgo(60), periodEnd: daysAgo(31), total: 990_000, currency: "IRT", status: "Overdue", issuedAt: daysAgo(31) },
  { id: "inv4", number: "KN-1403-0045", customerName: "شیرینی سرای پارس", periodStart: daysAgo(1), periodEnd: daysAhead(29), total: 0, currency: "IRT", status: "Draft", issuedAt: null },
];

export const servers: Server[] = [
  { id: "srv1", name: "prod-shared-01", hostingModel: "SharedManaged", environment: "Production", status: "Healthy", statusReason: null, provider: "hetzner", region: "fsn1", ipAddress: "10.12.5.101", dedicatedCustomerId: null, lastSeenAt: minutesAgo(1), decommissionedAt: null },
  { id: "srv2", name: "prod-cafe1-dedicated", hostingModel: "DedicatedManaged", environment: "Production", status: "Degraded", statusReason: "Disk above 80% for 20 minutes.", provider: "hetzner", region: "fsn1", ipAddress: "10.12.6.20", dedicatedCustomerId: "c1", lastSeenAt: minutesAgo(2), decommissionedAt: null },
  { id: "srv3", name: "staging-shared-01", hostingModel: "SharedManaged", environment: "Staging", status: "Healthy", statusReason: null, provider: "hetzner", region: "nbg1", ipAddress: "10.13.1.5", dedicatedCustomerId: null, lastSeenAt: minutesAgo(1), decommissionedAt: null },
  { id: "srv4", name: "legacy-alborz", hostingModel: "CustomerManaged", environment: "Production", status: "Offline", statusReason: "No heartbeat for 6 days.", provider: null, region: null, ipAddress: "185.4.20.77", dedicatedCustomerId: "c4", lastSeenAt: daysAgo(6), decommissionedAt: null },
];

/** What GET /monitoring/fleet answers: every machine's status and latest load. */
export const fleet: FleetOverview = {
  totalServers: 4,
  healthyServers: 2,
  degradedServers: 1,
  offlineServers: 1,
  unknownServers: 0,
  totalAgents: 4,
  onlineAgents: 3,
  offlineAgents: 1,
  openAlerts: 2,
  criticalAlerts: 1,
  servers: [
    { id: "srv1", name: "prod-shared-01", environment: "Production", hostingModel: "SharedManaged", status: "Healthy", statusReason: null, lastSeenAt: minutesAgo(1), cpuPercent: 42, memoryPercent: 61, diskPercent: 38 },
    { id: "srv2", name: "prod-cafe1-dedicated", environment: "Production", hostingModel: "DedicatedManaged", status: "Degraded", statusReason: "Disk above 80% for 20 minutes.", lastSeenAt: minutesAgo(2), cpuPercent: 85, memoryPercent: 78, diskPercent: 82 },
    { id: "srv3", name: "staging-shared-01", environment: "Staging", hostingModel: "SharedManaged", status: "Healthy", statusReason: null, lastSeenAt: minutesAgo(1), cpuPercent: 18, memoryPercent: 34, diskPercent: 22 },
    // Never reported, so it has no load at all - which the screen must show as
    // nothing rather than as zero.
    { id: "srv4", name: "legacy-alborz", environment: "Production", hostingModel: "CustomerManaged", status: "Offline", statusReason: "No heartbeat for 6 days.", lastSeenAt: daysAgo(6), cpuPercent: null, memoryPercent: null, diskPercent: null },
  ],
};

export const errorGroups: ErrorGroup[] = [
  { id: "eg1", storeName: "cafe1.ir", environment: "Production", exceptionType: "IntegrityError", title: "duplicate key value violates unique constraint", endpoint: "/api/orders/", occurrenceCount: 37, status: "New", firstSeenAt: minutesAgo(140), lastSeenAt: minutesAgo(4), firstSeenVersion: "4.2.0" , lastSeenVersion: "4.3.0", isRegression: true, incidentId: null },
  { id: "eg2", storeName: "cafe2.ir", environment: "Production", exceptionType: "OperationalError", title: "could not connect to server: Connection refused", endpoint: null, occurrenceCount: 12, status: "Acknowledged", firstSeenAt: daysAgo(1), lastSeenAt: minutesAgo(55), firstSeenVersion: "4.1.3" , lastSeenVersion: "4.1.3", isRegression: false, incidentId: "in1" },
  { id: "eg3", storeName: "parsbakery.ir", environment: "Production", exceptionType: "ValidationError", title: "invalid phone number format", endpoint: "/api/customers/", occurrenceCount: 4, status: "Resolved", firstSeenAt: daysAgo(9), lastSeenAt: daysAgo(6), firstSeenVersion: "4.1.0" , lastSeenVersion: "4.1.0", isRegression: false, incidentId: null },
  { id: "eg4", storeName: "staging.cafe1.ir", environment: "Staging", exceptionType: "TemplateSyntaxError", title: "Invalid block tag", endpoint: "/menu/", occurrenceCount: 2, status: "Ignored", firstSeenAt: daysAgo(2), lastSeenAt: daysAgo(2), firstSeenVersion: "4.3.0-rc1" , lastSeenVersion: "4.3.0-rc1", isRegression: false, incidentId: null },
];

export const incidents: Incident[] = [
  { id: "in1", reference: "INC-0142", title: "نصب تحلیل پیشرفته روی cafe1.ir ناموفق ماند", severity: "Critical", status: "Investigating", storeName: "cafe1.ir", serverName: "prod-cafe1-dedicated", openedAt: minutesAgo(12), resolvedAt: null },
  { id: "in2", reference: "INC-0141", title: "افزایش تاخیر پاسخ Redis", severity: "Warning", status: "Mitigated", storeName: null, serverName: "prod-shared-01", openedAt: minutesAgo(120), resolvedAt: null },
  { id: "in3", reference: "INC-0140", title: "قطع ارتباط با سرور مشتری‌محور", severity: "Critical", status: "Open", storeName: "alborz.ir", serverName: "legacy-alborz", openedAt: daysAgo(6), resolvedAt: null },
  { id: "in4", reference: "INC-0139", title: "توقف کوتاه صف پردازش", severity: "Info", status: "Resolved", storeName: null, serverName: "prod-shared-01", openedAt: daysAgo(3), resolvedAt: daysAgo(3) },
];

export const logs: LogEntry[] = [
  { id: "l1", timestamp: minutesAgo(2), level: "Error", service: "store-cafe1", storeName: "cafe1.ir", environment: "Production", message: "IntegrityError on POST /api/orders/", traceId: "4bf92f3577b34da6" },
  { id: "l2", timestamp: minutesAgo(3), level: "Information", service: "knight-api", storeName: null, environment: "Production", message: "Job j1 step migrate started", traceId: "0af7651916cd43dd" },
  { id: "l3", timestamp: minutesAgo(7), level: "Warning", service: "knight-agent", storeName: "cafe2.ir", environment: "Production", message: "Redis latency 145ms exceeds threshold", traceId: "1c2d3e4f5a6b7c8d" },
  { id: "l4", timestamp: minutesAgo(14), level: "Critical", service: "knight-api", storeName: "cafe1.ir", environment: "Production", message: "Feature installation failed; rollback requires manual intervention", traceId: "9d8c7b6a5f4e3d2c" },
  { id: "l5", timestamp: minutesAgo(31), level: "Information", service: "store-parsbakery", storeName: "parsbakery.ir", environment: "Production", message: "Health check passed", traceId: "aa11bb22cc33dd44" },
];

export const auditEntries: AuditEntry[] = [
  { id: "au1", occurredAt: minutesAgo(9), actor: "system", actorType: "System", action: "feature.installed", target: "knight-feature-analytics 1.4.0 @ cafe3.ir", customerName: "شیرینی سرای پارس", result: "Success", ipAddress: null, correlationId: "0HMV9A0011" },
  { id: "au2", occurredAt: minutesAgo(12), actor: "system", actorType: "System", action: "feature.installation_failed", target: "knight-feature-analytics 1.4.0 @ cafe1.ir", customerName: "کافه وان", result: "Failure", ipAddress: null, correlationId: "0HMV9A1B77" },
  { id: "au3", occurredAt: minutesAgo(64), actor: "ali@knight.local", actorType: "User", action: "subscription.changed", target: "کافه وان → حرفه‌ای", customerName: "کافه وان", result: "Success", ipAddress: "192.168.1.105", correlationId: "0HMV99AA21" },
  { id: "au4", occurredAt: minutesAgo(190), actor: "sara@knight.local", actorType: "User", action: "store.credentials.issued", target: "cafe4.ir", customerName: "قنادی نوین", result: "Success", ipAddress: "192.168.1.118", correlationId: "0HMV98BB55" },
  { id: "au5", occurredAt: minutesAgo(420), actor: "ali@knight.local", actorType: "User", action: "feature.published", target: "knight-feature-ai-reports 2.0.1", customerName: null, result: "Success", ipAddress: "192.168.1.105", correlationId: "0HMV97CC90" },
  { id: "au6", occurredAt: daysAgo(1), actor: "unknown", actorType: "Store", action: "ingest.rejected", target: "environment mismatch", customerName: "رستوران البرز", result: "Failure", ipAddress: "203.0.113.42", correlationId: "0HMV96DD12" },
];

export const admins: AdminUser[] = [
  { id: "u1", displayName: "علی محمدی", email: "ali@knight.local", scope: "Platform", customerName: null, roles: ["SuperAdmin"], mfaEnabled: true, status: "Active", lastLoginAt: minutesAgo(5) },
  { id: "u2", displayName: "سارا رضایی", email: "sara@knight.local", scope: "Platform", customerName: null, roles: ["Admin"], mfaEnabled: true, status: "Active", lastLoginAt: minutesAgo(45) },
  { id: "u3", displayName: "رضا کریمی", email: "reza@knight.local", scope: "Platform", customerName: null, roles: ["Developer"], mfaEnabled: false, status: "Active", lastLoginAt: daysAgo(1) },
  { id: "u4", displayName: "مالک کافه وان", email: "owner@cafe1.ir", scope: "Customer", customerName: "کافه وان", roles: ["CustomerOwner"], mfaEnabled: false, status: "Active", lastLoginAt: daysAgo(2) },
  { id: "u5", displayName: "پشتیبان کافه تو", email: "staff@cafe2.ir", scope: "Customer", customerName: "کافه تو", roles: ["CustomerStaff"], mfaEnabled: false, status: "Suspended", lastLoginAt: daysAgo(20) },
];

export const roles: Role[] = [
  { id: "r1", name: "SuperAdmin", scope: "Platform", isSystem: true, permissionCount: 34, userCount: 1 },
  { id: "r2", name: "Admin", scope: "Platform", isSystem: true, permissionCount: 28, userCount: 1 },
  { id: "r3", name: "Developer", scope: "Platform", isSystem: true, permissionCount: 14, userCount: 1 },
  { id: "r4", name: "Support", scope: "Platform", isSystem: true, permissionCount: 9, userCount: 0 },
  { id: "r5", name: "CustomerOwner", scope: "Customer", isSystem: true, permissionCount: 11, userCount: 1 },
  { id: "r6", name: "CustomerStaff", scope: "Customer", isSystem: true, permissionCount: 6, userCount: 1 },
];

export const platformServices = [
  { key: "api", name: "Knight API", detail: "Core service", status: "Healthy", metrics: [["Uptime", "99.99%"], ["Latency", "45ms"]] },
  { key: "db", name: "PostgreSQL", detail: "Primary DB", status: "Healthy", metrics: [["Load", "24%"], ["Conn", "1,204"]] },
  { key: "redis", name: "Redis", detail: "In-memory cache", status: "Degraded", metrics: [["Hit rate", "98.2%"], ["Mem", "4.2 GB"]] },
  { key: "registry", name: "Package Registry", detail: "Feature artifacts", status: "Healthy", metrics: [["Artifacts", "38"], ["Pulls/day", "112"]] },
  { key: "storage", name: "Object Storage", detail: "S3 compatible", status: "Healthy", metrics: [["Capacity", "68%"], ["I/O", "450 ops/s"]] },
  { key: "workers", name: "Background Workers", detail: "Job queue", status: "Healthy", metrics: [["Pending", "14"], ["Processed", "1.2M"]] },
] as const;

export const reports = [
  { key: "revenue", name: "درآمد ماهانه", description: "جمع صورتحساب‌های صادرشده به تفکیک ماه.", updatedAt: daysAgo(1) },
  { key: "adoption", name: "پذیرش قابلیت‌ها", description: "نسبت نصب موفق به تعداد entitlement هر قابلیت.", updatedAt: minutesAgo(90) },
  { key: "reliability", name: "پایداری فروشگاه‌ها", description: "درصد در دسترس بودن و تعداد رخداد هر فروشگاه.", updatedAt: minutesAgo(30) },
  { key: "delivery", name: "عملکرد تحویل قابلیت", description: "میانگین زمان نصب، نرخ شکست و rollback.", updatedAt: minutesAgo(15) },
];
