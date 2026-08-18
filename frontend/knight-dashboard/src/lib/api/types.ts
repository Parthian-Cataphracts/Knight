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
  mfaEnabled: boolean;
  /** False while a required second factor is still outstanding for this session. */
  mfaSatisfied: boolean;
}

export interface LoginRequest {
  email: string;
  password: string;
  /** Six-digit TOTP code, sent on the second leg of a login that requires MFA. */
  mfaCode?: string;
}

/**
 * The API answers a login with a status rather than only tokens: an account may
 * still owe a second factor, or may not have enrolled one yet
 * (docs/authentication.md section 1). Tokens and the user are absent in those
 * cases, so both are nullable here.
 */
export type LoginStatus = "succeeded" | "mfa_required" | "mfa_enrollment_required";

export interface LoginResponse {
  status: LoginStatus;
  accessToken: string | null;
  expiresAt: string | null;
  refreshToken: string | null;
  user: CurrentUser | null;
}

export interface MfaEnrollmentResponse {
  secret: string;
  enrollmentUri: string;
}

export interface MfaCodeRequest {
  code: string;
}

/**
 * The dashboard's landing figures, as the control plane actually reports them
 * today. Server metrics, alerts and feature delivery arrive in later phases and
 * are deliberately absent rather than reported as zeros that would read like
 * measurements.
 */
export interface DashboardOverview {
  customers: { total: number; active: number; suspended: number; prospect: number; archived: number };
  stores: { total: number; connected: number; degraded: number; disconnected: number; notRegistered: number };
  subscriptions: {
    total: number;
    active: number;
    trial: number;
    pastDue: number;
    suspended: number;
    activeEntitlements: number;
  };
  billing: {
    draft: number;
    issued: number;
    overdue: number;
    paid: number;
    outstandingTotal: number;
    currency: string | null;
  };
  recentActivity: {
    id: string;
    action: string;
    targetType: string;
    targetId: string | null;
    actor: string | null;
    occurredAt: string;
  }[];
}
