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
  contactEmail: string;
  status: CustomerStatus;
  /** Absent until the customer has a subscription. */
  planKey: string | null;
  storeCount: number;
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

export interface Server {
  id: string;
  name: string;
  hostingModel: HostingModel;
  environment: Environment;
  ipAddress: string;
  status: HealthState;
  cpuPercent: number;
  memoryPercent: number;
  diskPercent: number;
  uptimePercent: number;
  agentVersion: string | null;
  storeCount: number;
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
  roles: string[];
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
  scope: "Platform" | "Customer";
  isSystem: boolean;
  permissionCount: number;
  userCount: number;
}
