import type { ArtifactUpload } from "./domain";
import { ApiError, type ProblemDetails } from "./problem";
import { mockFetch } from "./mock";

const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "/api/v1";
const USE_MOCKS = import.meta.env.VITE_USE_MOCKS === "true";

let accessToken: string | null = null;
let onUnauthorized: (() => void) | null = null;
let refreshSession: (() => Promise<void>) | null = null;

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

/**
 * How the client renews an expired access token.
 *
 * Injected rather than imported because the session store already depends on
 * this module, and the alternative to a cycle is every caller remembering to
 * handle 401 itself — which is exactly what went wrong before this existed.
 */
export function setSessionRefresher(refresh: () => Promise<void>): void {
  refreshSession = refresh;
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
 *
 * An expired access token is recovered from rather than surfaced. Access tokens
 * are deliberately short-lived and the refresh token lives in an HttpOnly
 * cookie, so the first 401 on an ordinary call means "renew this", not "you are
 * signed out" — and before this retry existed, an operator who left a form open
 * long enough had their save rejected and their typing thrown away by a bounce
 * to the login screen.
 */
export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  return send<T>(path, options, true);
}

/**
 * Fetches a file the server generates — a CSV export, say — as a Blob, carrying
 * the bearer token like every other call and recovering from one expired token.
 *
 * Its own function rather than {@link apiRequest} because the body is not JSON:
 * parsing a CSV as JSON would throw on the first comma. The caller turns the Blob
 * into a download; the server does not need to know it became one.
 */
export async function apiDownload(path: string, mayRetry = true): Promise<Blob> {
  const headers: Record<string, string> = {
    "X-Correlation-Id": correlationId(),
  };
  if (accessToken) headers["Authorization"] = `Bearer ${accessToken}`;

  const response = await fetch(`${BASE_URL}${path}`, {
    method: "GET",
    headers,
    credentials: "include",
  });

  if (!response.ok) {
    if (response.status === 401 && mayRetry && refreshSession) {
      try {
        await refreshSession();
      } catch {
        onUnauthorized?.();
        throw new ApiError(response.status, { status: 401 } as ProblemDetails);
      }

      return apiDownload(path, false);
    }

    if (response.status === 401) onUnauthorized?.();
    throw new ApiError(response.status, { status: response.status } as ProblemDetails);
  }

  return response.blob();
}

/**
 * Uploads an already-signed package and answers what KNIGHT hashed it to.
 *
 * Its own function rather than a body on {@link apiRequest}, because a multipart
 * upload must not carry a JSON content type and must not be serialised. The
 * digest in the answer is computed server-side from the stored bytes — the
 * publish request that follows declares it, and the signature is checked against
 * it, which only means anything because the middle link is not the uploader's
 * word.
 */
export async function uploadArtifact(file: File): Promise<ArtifactUpload> {
  const form = new FormData();
  form.append("file", file);

  const headers: Record<string, string> = {
    Accept: "application/json",
    "X-Correlation-Id": correlationId(),
  };
  if (accessToken) headers["Authorization"] = `Bearer ${accessToken}`;

  const response = await fetch(`${BASE_URL}/artifacts`, {
    method: "POST",
    headers,
    credentials: "include",
    body: form,
  });

  const text = await response.text();
  const payload: unknown = text ? JSON.parse(text) : null;

  if (!response.ok) throw new ApiError(response.status, (payload ?? {}) as ProblemDetails);

  return payload as ArtifactUpload;
}

async function send<T>(path: string, options: RequestOptions, mayRetry: boolean): Promise<T> {
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
    if (response.status === 401) {
      // The auth endpoints are excluded: a failed sign-in or a refresh that was
      // itself refused is an answer, not a stale token, and retrying it would
      // turn one rejection into two — or into a loop.
      const isAuthCall = path.startsWith("/auth/");

      if (mayRetry && !isAuthCall && refreshSession) {
        try {
          await refreshSession();
        } catch {
          onUnauthorized?.();
          throw new ApiError(response.status, (payload ?? {}) as ProblemDetails);
        }

        // Retried once, never twice: a second 401 after a successful refresh is
        // the server saying no, not a token that needs renewing again.
        return send<T>(path, options, false);
      }

      onUnauthorized?.();
    }

    throw new ApiError(response.status, (payload ?? {}) as ProblemDetails);
  }

  return payload as T;
}
