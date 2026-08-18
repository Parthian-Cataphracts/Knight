/** Mirrors Knight.Contracts; to be generated from OpenAPI once the API exists. */

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export type Environment = "Development" | "Staging" | "Production";
export type HealthState = "Healthy" | "Degraded" | "Offline" | "Unknown";

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  customerId: string | null;
  roles: string[];
  permissions: string[];
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresIn: number;
  user: CurrentUser;
}

export interface DashboardOverview {
  customers: { active: number; suspended: number };
  stores: { total: number; connected: number; degraded: number; disconnected: number };
  alerts: { open: number; critical: number };
  services: { name: string; state: HealthState; latencyMs: number | null }[];
  resources: { cpuPercent: number; memoryPercent: number; diskPercent: number };
  featureDelivery: {
    runningJobs: number;
    failedInstallations: number;
    entitledNotInstalled: number;
  };
  openAlerts: {
    id: string;
    severity: "critical" | "warning" | "info";
    title: string;
    detail: string;
    raisedAt: string;
  }[];
  recentActivity: {
    id: string;
    action: string;
    target: string;
    actor: string;
    occurredAt: string;
  }[];
}
