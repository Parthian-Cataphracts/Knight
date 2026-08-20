import type { ReactElement, ReactNode } from "react";
import { render, type RenderResult } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { I18nextProvider } from "react-i18next";
import i18n from "@/i18n";
import { useAuthStore } from "@/store/auth";

/**
 * Renders a screen with the providers it cannot run without.
 *
 * Retries are off and the cache is per-test. A retrying query turns a component
 * test that should fail in milliseconds into one that hangs, and a shared cache
 * makes tests pass or fail depending on what ran before them — both are the kind
 * of flakiness that gets a suite ignored rather than fixed.
 */
export function renderScreen(
  ui: ReactElement,
  { permissions = [] as string[], route = "/" } = {},
): RenderResult {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
      mutations: { retry: false },
    },
  });

  // Permissions drive what the UI offers, never what it is allowed to do — the
  // API enforces authorization. Tests set them directly so a screen's
  // permission-aware rendering can be asserted without a login round trip.
  useAuthStore.setState({
    status: "authenticated",
    user: {
      id: "test-user",
      email: "tester@knight.test",
      displayName: "Tester",
      customerId: null,
      roles: ["Admin"],
      permissions,
      mfaEnabled: true,
      mfaSatisfied: true,
    },
  });

  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <I18nextProvider i18n={i18n}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>
        </QueryClientProvider>
      </I18nextProvider>
    );
  }

  return render(ui, { wrapper: Wrapper });
}

/**
 * Answers the given payload for any path that matches, and a 404 otherwise.
 *
 * Deliberately keyed by path fragment rather than by an exact URL: a screen that
 * quietly starts requesting an extra endpoint should show up as a 404 in the
 * test rather than silently reusing another route's response.
 */
export function mockApi(responses: Record<string, unknown>): void {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === "string" ? input : input.toString();
      const match = Object.keys(responses).find((path) => url.includes(path));

      if (match === undefined) {
        return new Response(JSON.stringify({ title: "Not found" }), {
          status: 404,
          headers: { "Content-Type": "application/json" },
        });
      }

      return new Response(JSON.stringify(responses[match]), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }),
  );
}

/** Wraps items in the paged envelope every collection endpoint returns. */
export function paged<T>(items: T[]): { items: T[]; page: number; pageSize: number; totalCount: number } {
  return { items, page: 1, pageSize: items.length, totalCount: items.length };
}
