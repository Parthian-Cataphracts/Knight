/** Domain contracts for the control-plane screens. Mirrors Knight.Contracts. */
import type { Environment, HealthState } from "./types";

export type CustomerStatus = "Prospect" | "Active" | "Suspended" | "Archived";
export type StoreStatus = "Provisioning" | "Active" | "Suspended" | "Archived";
export type IntegrationStatus =
  | "NotRegistered"
  | "Pending"
  | "Connected"
  | "Degraded"
  | "Disconnected";
export type HostingModel = "SharedManaged" | "DedicatedManaged" | "CustomerManaged";

export interface Customer {
  id: string;
  name: string;
<<<<<<< HEAD
=======

  /**
   * The three the API has always returned and the dashboard used not to declare.
   * Leaving them out did not merely hide them: the edit form PATCHes the fields
   * it knows about, and the update overwrites the profile wholesale, so every
   * rename silently blanked the legal name and the phone number.
   */
  legalName: string | null;
  phone: string | null;
  notes: string | null;

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
  contactEmail: string;
  status: CustomerStatus;
  /** Absent until the customer has a subscription. */
  planKey: string | null;
  storeCount: number;

  /**
   * A negotiated retention window in days that replaces the plan's. Null means
   * the plan decides — which is not the same as "no retention".
   */
  dataRetentionOverrideDays: number | null;
  createdAt: string;
}

export interface Store {
  id: string;
  customerId: string;
  customerName: string;
  name: string;
  primaryDomain: string;
  environment: Environment;
  applicationVersion: string | null;
  integrationStatus: IntegrationStatus;
  hostingModel: HostingModel;
  status: StoreStatus;
  /**
   * Null where the response was not built by a caller that counted them — a
   * store just created or just suspended, for instance. Zero is a different
   * claim and means the store really is running nothing.
   */
  installedFeatureCount: number | null;
  lastSeenAt: string | null;

<<<<<<< HEAD
=======
  /** The machine this store runs on. Null when nobody has placed it yet. */
  serverId: string | null;

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
  /** True when the store must present a client certificate as well as its credential. */
  requiresMutualTls: boolean;

  /** A public identifier of the bound certificate, not a secret. Null when none is bound. */
  mutualTlsThumbprint: string | null;
}

// --- Provisioning -----------------------------------------------------------

export type ProvisioningState =
  | "Running"
  | "AwaitingOperator"
  | "Succeeded"
  | "Failed"
  | "Cancelled";

export type ProvisioningStepStatus = "Pending" | "Waiting" | "Succeeded" | "Failed" | "Skipped";

export interface ProvisioningStep {
  sequence: number;
  name: string;

  /** Automatic steps are things KNIGHT checks; manual ones are things a person does. */
  mode: "Automatic" | "Manual";
  status: ProvisioningStepStatus;

  /** What the step is waiting for, or what it did. Written to be acted on. */
  detail: string | null;
  errorCode: string | null;
  completedBy: string | null;
  startedAt: string;
  completedAt: string | null;
}

export interface ProvisioningJob {
  id: string;
  storeId: string;
  customerId: string;
  kind: "Provision" | "Deprovision";
  state: ProvisioningState;
  awaitingOperator: boolean;
  currentStep: string | null;
  completedStepCount: number;
  totalStepCount: number;
  baseImageVersion: string | null;

  /** When a deprovisioned store's data may be purged. Null on a provisioning run. */
  retainUntil: string | null;
  failureCode: string | null;
  failureMessage: string | null;
  createdAt: string;
  completedAt: string | null;
  steps: ProvisioningStep[];
}

export interface StoreBackup {
  id: string;
  storeId: string;
  status: "Succeeded" | "Failed" | "Running";
  kind: "Scheduled" | "Manual" | "PreDeployment";
  startedAt: string;
  completedAt: string | null;
  reportedAt: string;
  sizeBytes: number | null;

  /** A reference an operator resolves elsewhere — never a link KNIGHT can follow. */
  location: string | null;
  detail: string | null;
  durationSeconds: number | null;
}

/** A published base store image: signed, digest-verified, and pinning a store version. */
export interface StoreImage {
  id: string;
  version: string;
  storeVersion: string;
  status: "Draft" | "Published" | "Yanked";
  artifactDigest: string;
  artifactSizeBytes: number;
  signingKeyId: string;
  releaseNotes: string | null;
  createdAt: string;
  publishedAt: string | null;
  yankedAt: string | null;
  yankReason: string | null;
}

/** What an artifact upload answers: where it landed and what KNIGHT hashed it to. */
export interface ArtifactUpload {
  packageReference: string;
  digest: string;
  sizeBytes: number;
}

// --- Feature registry -------------------------------------------------------

export type FeatureStatus = "Draft" | "Published" | "Deprecated" | "Withdrawn";
export type VersionStatus = "Draft" | "Published" | "Yanked";

export interface Feature {
  id: string;
  slug: string;
  name: string;
  description: string;
  category: string;
  status: FeatureStatus;
  isOptional: boolean;
  requiresDedicatedInfrastructure: boolean;
  /** Null until the feature registry exists (phase 3.5). */
  latestVersion: string | null;
  /** Null until delivery exists: "not knowable yet" is not the same as zero. */
  installCount: number | null;
  entitledCount: number;
  plans: string[];
}

export interface FeatureVersion {
  id: string;
  featureId: string;
  version: string;
  status: VersionStatus;
  packageReference: string;
  artifactDigest: string;
  signed: boolean;
  storeVersionRange: string;
  dependencies: { slug: string; range: string }[];
  migrations: { required: boolean; reversible: boolean; estimatedSeconds: number };
  publishedAt: string | null;
  publishedBy: string | null;
}

// --- Installation and jobs --------------------------------------------------

export type InstallationState =
  | "NotInstalled"
  | "Pending"
  | "Installing"
  | "Installed"
  | "Updating"
  | "RollingBack"
  | "Failed"
  | "Disabled"
  | "Uninstalling";

export interface Installation {
  id: string;
  storeId: string;
  storeName: string | null;
  featureId: string;
  featureName: string | null;
  featureSlug: string;

  /** Entitlement and installation are separate facts; the screen shows both columns. */
  entitled: boolean;
  isEnabled: boolean;
  installedVersion: string | null;
  targetVersion: string | null;
  previousVersion: string | null;
  state: InstallationState;
  health: HealthState;
  currentJobId: string | null;
  failureCode: string | null;
  failureMessage: string | null;
  rollbackOutcome: "NotAttempted" | "RolledBack" | "PartiallyRolledBack" | "ManualInterventionRequired";
  blockingReason: string | null;

  /** True when a database is in a state KNIGHT refused to guess about. */
  requiresManualIntervention: boolean;
  installedAt: string | null;
  disabledAt: string | null;
  lastTransitionAt: string;
}

export type JobType =
  | "Install"
  | "Upgrade"
  | "ApplyConfiguration"
  | "Enable"
  | "Disable"
  | "Uninstall"
  | "Rollback"
  | "HealthCheck"
  | "Provision"
  | "Backup";

/** The states the delivery engine actually records. */
export type JobStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled";

export interface JobStep {
  sequence: number;
  name: string;
  status: "Running" | "Succeeded" | "Failed" | "Skipped";
  output: string | null;
  errorCode: string | null;
  durationMilliseconds: number | null;
  reportCount: number;
  startedAt: string;
  completedAt: string | null;
}

/** What the job detail endpoint returns: the job, plus its steps. */
export interface JobDetail {
  job: Job;
  steps: JobStep[];
}

export interface Job {
  id: string;
  storeId: string;
  storeName: string | null;
  featureId: string;
  featureSlug: string;
  type: JobType;

  /** The API calls this "state"; the screens speak of a job's status. Same field. */
  state: JobStatus;
  targetVersion: string | null;

  /** Why the job exists. A job an operator asked for reads very differently after an incident. */
  trigger: "Manual" | "Entitlement" | "Provisioning" | "Reconciliation" | "Schedule";
  completedStepCount: number;
  totalStepCount: number;
  attemptCount: number;
  maxAttempts: number;
  failureCode: string | null;
  failureMessage: string | null;
  rollbackOutcome: "NotAttempted" | "RolledBack" | "PartiallyRolledBack" | "ManualInterventionRequired";
  queuedAt: string;
  claimedAt: string | null;
  completedAt: string | null;
  correlationId: string;
}

// --- Commercial -------------------------------------------------------------

export interface Plan {
  id: string;
  key: string;
  name: string;
  description: string;
  basePrice: number;
  currency: string;

  /** Whether the plan may currently be sold. Withdrawing one leaves existing subscriptions alone. */
  isActive: boolean;
  sortOrder: number;
  customerCount: number;
  includedFeatures: string[];
  optionalFeatures: string[];
}

export interface EntitlementMatrixRow {
  featureSlug: string;
  featureName: string;
  values: Record<string, string | boolean>;
}

export interface Subscription {
  id: string;
  customerId: string;
  customerName: string;
  planKey: string;
  planName: string;
  status: "Trial" | "Active" | "PastDue" | "Suspended" | "Cancelled";
  optionalFeatures: number;
  monthlyTotal: number;
  currency: string;
  currentPeriodEnd: string;
}

export interface Invoice {
  id: string;
  number: string;
  customerName: string;
  periodStart: string;
  periodEnd: string;
  total: number;
  currency: string;
  status: "Draft" | "Issued" | "Paid" | "Void" | "Overdue";
  issuedAt: string | null;
}

// --- Infrastructure and observability ---------------------------------------

<<<<<<< HEAD
=======
/**
 * A server as GET /servers returns it.
 *
 * It carries no load figures, and used to be declared here as though it did -
 * cpuPercent, memoryPercent, diskPercent, uptimePercent, agentVersion and
 * storeCount were all fiction, and the infrastructure screen rendered undefined
 * for every one of them against a real deployment. Load lives on the fleet
 * overview below, which reports every server in one batched call.
 */
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
export interface Server {
  id: string;
  name: string;
  hostingModel: HostingModel;
  environment: Environment;
<<<<<<< HEAD
  ipAddress: string;
  status: HealthState;
  cpuPercent: number;
  memoryPercent: number;
  diskPercent: number;
  uptimePercent: number;
  agentVersion: string | null;
  storeCount: number;
=======
  status: HealthState;

  /** Why it is in this status, in words. Null when it is simply healthy. */
  statusReason: string | null;

  provider: string | null;
  region: string | null;
  ipAddress: string | null;

  /** The customer this machine is dedicated to. Null means it is shared. */
  dedicatedCustomerId: string | null;

  lastSeenAt: string | null;
  decommissionedAt: string | null;
}

/** One server's latest load, from the fleet overview. */
export interface FleetServer {
  id: string;
  name: string;
  environment: Environment;
  hostingModel: HostingModel;
  status: HealthState;
  statusReason: string | null;
  lastSeenAt: string | null;

  /** Null until the machine's agent has reported at least once. */
  cpuPercent: number | null;
  memoryPercent: number | null;
  diskPercent: number | null;
}

/** GET /monitoring/fleet — every server's status and load, in one call. */
export interface FleetOverview {
  totalServers: number;
  healthyServers: number;
  degradedServers: number;
  offlineServers: number;
  unknownServers: number;
  totalAgents: number;
  onlineAgents: number;
  offlineAgents: number;
  openAlerts: number;
  criticalAlerts: number;
  servers: FleetServer[];
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
}

export interface ErrorGroup {
  id: string;
  storeName: string;
  environment: Environment;
  exceptionType: string;
  title: string;
  endpoint: string | null;
  occurrenceCount: number;
  status: "New" | "Acknowledged" | "Resolved" | "Ignored";
  firstSeenAt: string;
  lastSeenAt: string;
  firstSeenVersion: string | null;
  lastSeenVersion: string | null;

  /** Resolved once, and back again. A fix that did not hold is not the same as a new problem. */
  isRegression: boolean;
  incidentId: string | null;
}

export interface Incident {
  id: string;
  reference: string;
  title: string;
  severity: "Critical" | "Warning" | "Info";
  status: "Open" | "Investigating" | "Mitigated" | "Resolved";
  storeName: string | null;
  serverName: string | null;
  openedAt: string;
  resolvedAt: string | null;
}

export interface LogEntry {
  id: string;
  timestamp: string;
  level: "Debug" | "Information" | "Warning" | "Error" | "Critical";
  service: string;
  storeName: string | null;
  environment: Environment;
  message: string;
  traceId: string | null;
}

export interface AuditEntry {
  id: string;
  occurredAt: string;
  actor: string;
  actorType: "User" | "System" | "Store" | "Agent";
  action: string;
  target: string;
  customerName: string | null;
  result: "Success" | "Failure";
  ipAddress: string | null;
  correlationId: string;
}

export interface AdminUser {
  id: string;
  displayName: string;
  email: string;
  scope: "Platform" | "Customer";
  customerName: string | null;
<<<<<<< HEAD
  roles: string[];
=======

  /** Role names, for display. */
  roles: string[];

  /** The same roles by id, which is what setting them takes. */
  roleIds: string[];

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
  mfaEnabled: boolean;
  status: "Active" | "Suspended";
  /**
   * Set when the account last signed in. Named for what the API sends, which is
   * also the more honest name: it is not a presence signal, and an operator who
   * signed in this morning and has been working since still reads as this
   * morning.
   */
  lastLoginAt: string | null;
}

export interface Role {
  id: string;
  name: string;
<<<<<<< HEAD
  scope: "Platform" | "Customer";
  isSystem: boolean;
=======
  description: string | null;
  scope: "Platform" | "Customer";
  isSystem: boolean;

  /** The keys this role grants. The API has always sent them; nothing read them. */
  permissions: string[];

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
  permissionCount: number;
  userCount: number;
}

<<<<<<< HEAD
=======
/**
 * The dry run behind the install preview: POST /installations/plan.
 *
 * A plan is either steps or failures, never both - the resolver refuses to
 * produce half a plan, because a partial install is worse than none.
 */
export interface InstallPlanStep {
  featureId: string;
  versionId: string;
  slug: string;
  name: string;
  version: string;

  /** The version the store is on now. Null when the Feature is not installed. */
  installedVersion: string | null;

  action: "Install" | "Upgrade" | "AlreadySatisfied" | "DowngradeRefused";

  /** True for the Feature that was asked for; the rest are its dependencies. */
  isRoot: boolean;

  requiredBy: string;

  migrationsRequired: boolean;

  /**
   * Declared by the Feature's author and treated as binding: it is the single
   * input deciding whether a failed upgrade can put the database back.
   */
  migrationsReversible: boolean;

  migrationSeconds: number;
  requiresRestart: boolean;
}

export interface InstallPlanFailure {
  /** What the dashboard branches on. */
  code: string;
  slug: string;
  /** What a person reads. */
  message: string;
}

export interface InstallPlan {
  isSuccessful: boolean;
  steps: InstallPlanStep[];
  failures: InstallPlanFailure[];
}

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
// --- Staged rollouts -------------------------------------------------------

export type RolloutState = "Planned" | "InProgress" | "Halted" | "Completed" | "Cancelled";

export type RolloutWaveState = "Pending" | "Dispatched" | "Completed";

export type RolloutTargetState = "Pending" | "Dispatched" | "Succeeded" | "Failed";

export interface RolloutTarget {
  storeId: string;
  state: RolloutTargetState;
  jobId: string | null;
  detail: string | null;
  completedAt: string | null;
}

export interface RolloutWave {
  id: string;
  ordinal: number;
  /** True for wave 0 — the single store an unproven version reaches first. */
  isCanary: boolean;
  state: RolloutWaveState;
  dispatchedAt: string | null;
  completedAt: string | null;
  targets: RolloutTarget[];
}

/**
 * A staged rollout of one Feature version across the fleet
 * (docs/adr/0028-staged-rollouts-with-a-single-store-canary.md).
 *
 * `haltReason` is the field the screen must never hide: a rollout that stopped
 * looks the same as one that is between waves unless the reason is shown.
 */
export interface Rollout {
  id: string;
  featureId: string;
  featureSlug: string;
  targetVersion: string;
  state: RolloutState;
  failureThreshold: number;
  totalStores: number;
  succeededStores: number;
  failedStores: number;
  haltReason: string | null;
  createdBy: string;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  waves: RolloutWave[];
}
