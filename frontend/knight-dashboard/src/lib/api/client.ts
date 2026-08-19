import { ApiError, type ProblemDetails } from "./problem";
import { mockFetch } from "./mock";

const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "/api/v1";
const USE_MOCKS = import.meta.env.VITE_USE_MOCKS === "true";

let accessToken: string | null = null;
let onUnauthorized: (() => void) | null = null;

export function setAccessToken(token: string | null): void {
  accessToken = token;
}

/**
 * The current bearer token, for the one caller that cannot go through
 * <see cref="apiRequest"/>: the realtime connection, which must hand the token
 * to its own transport. Kept as a getter rather than exported state so nothing
 * can hold a stale copy across a refresh.
 */
export function getAccessToken(): string | null {
  return accessToken;
}

export function setUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler;
}

function correlationId(): string {
  return crypto.randomUUID();
}

export interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  body?: unknown;
  signal?: AbortSignal;
  idempotencyKey?: string;
}

/**
 * The single entry point to the KNIGHT API. Components never call fetch
 * directly - see docs/frontend-architecture.md section 3.
 */
export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = "GET", body, signal, idempotencyKey } = options;

  const headers: Record<string, string> = {
    Accept: "application/json",
    "X-Correlation-Id": correlationId(),
  };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (accessToken) headers["Authorization"] = `Bearer ${accessToken}`;
  if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;

  const request: RequestInit = {
    method,
    headers,
    credentials: "include",
  };
  if (body !== undefined) request.body = JSON.stringify(body);
  if (signal) request.signal = signal;

  const response = USE_MOCKS
    ? await mockFetch(path, method, body)
    : await fetch(`${BASE_URL}${path}`, request);

  if (response.status === 204) return undefined as T;

  const text = await response.text();
  const payload: unknown = text ? JSON.parse(text) : null;

  if (!response.ok) {
    if (response.status === 401) onUnauthorized?.();
    throw new ApiError(response.status, (payload ?? {}) as ProblemDetails);
  }

  return payload as T;
}
