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
  planKey: string;
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
  installedFeatureCount: number;
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
  latestVersion: string | null;
  installCount: number;
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
  storeName: string;
  featureId: string;
  featureName: string;
  featureSlug: string;
  entitled: boolean;
  installedVersion: string | null;
  desiredVersion: string | null;
  state: InstallationState;
  isEnabled: boolean;
  health: HealthState;
  blockingReason: string | null;
  rollbackOutcome: "None" | "Succeeded" | "ManualInterventionRequired" | null;
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

export type JobStatus =
  | "Queued"
  | "Claimed"
  | "Running"
  | "Succeeded"
  | "Failed"
  | "Cancelled"
  | "TimedOut";

export interface JobStep {
  index: number;
  name: string;
  status: "Pending" | "Running" | "Succeeded" | "Failed" | "Skipped";
  output: string | null;
}

export interface Job {
  id: string;
  type: JobType;
  status: JobStatus;
  storeId: string;
  storeName: string;
  target: string;
  currentStep: number;
  totalSteps: number;
  steps: JobStep[];
  errorCode: string | null;
  rollbackOutcome: "None" | "Succeeded" | "ManualInterventionRequired" | null;
  queuedAt: string;
  startedAt: string | null;
  finishedAt: string | null;
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
  firstSeenVersion: string;
}

export interface Incident {
  id: string;
  reference: string;
  title: string;
  severity: "critical" | "warning" | "info";
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
  lastSeenAt: string | null;
}

export interface Role {
  id: string;
  name: string;
  scope: "Platform" | "Customer";
  isSystem: boolean;
  permissionCount: number;
  userCount: number;
}
