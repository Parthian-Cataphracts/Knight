import {
  useMutation,
  useQueryClient,
  useQuery,
  type UseMutationResult,
  type UseQueryResult,
} from "@tanstack/react-query";
import { apiRequest, type RequestOptions } from "./client";
import type { Paged } from "./types";

/** The API's ceiling (`PagedRequest.MaxPageSize`). Asking for more is clamped. */
const MAX_PAGE_SIZE = 100;

/**
 * How many pages this will follow before it stops.
 *
 * A guard rather than a limit anybody should reach: at a hundred rows a page
 * that is two thousand of something, and every screen using this renders the
 * whole collection into a table. A list that genuinely grows past it needs
 * server-side paging in the screen, not a bigger number here.
 */
const MAX_PAGES = 20;

/**
 * Reads a paged collection endpoint, **following the pages**.
 *
 * Every screen built on this filters, counts and renders the whole collection
 * client-side, so a hook that returned only the first page did not return a
 * short list — it returned a wrong one. Phase 16 is where that surfaced: the
 * catalogue passed twenty-five features, the Features screen showed twenty-five
 * of twenty-nine, and its "Draft: 1" tab was counting a page rather than a
 * catalogue while a Draft feature sat invisible behind it. No test could see it,
 * because a fixture returns everything on page one.
 *
 * Keys are the path, so caches stay distinct.
 *
 * `enabled` exists for the detail panels, whose path depends on a selection that
 * may not have been made yet. Without it they request a placeholder id on every
 * page load — which a fixture happily answers and the real API correctly
 * refuses, so the fault only ever appears in a browser against a live server.
 */
export function useCollection<T>(path: string, enabled = true): UseQueryResult<T[], Error> {
  return useQuery({
    queryKey: ["collection", path],
    queryFn: async () => {
      const first = await apiRequest<Paged<T>>(paged(path, 1));
      const items = [...first.items];

      // A caller that named its own page size has asked for that many — the
      // notification centre wants the fifty most recent, not every notification
      // this platform has ever sent — so it gets one page and no more.
      //
      // Otherwise: `totalPages` is what the server says, not what the client
      // hoped. An endpoint that does not paginate answers with one page and this
      // stops after the request it would have made anyway.
      const pages = asked(path, "pageSize") ? 1 : Math.min(first.totalPages || 1, MAX_PAGES);

      for (let page = 2; page <= pages; page += 1) {
        const next = await apiRequest<Paged<T>>(paged(path, page));
        items.push(...next.items);
      }

      return items;
    },
    enabled,
  });
}

/**
 * Adds paging to a path that may already carry a query string of its own.
 *
 * A caller's own `pageSize` wins and is not duplicated. Appending a second one
 * does not override the first: the query string arrives as `pageSize=50&
 * pageSize=100`, which model binding reads as the single value "50,100" and
 * fails on — a 500 from what is really a malformed request, and one this hook
 * would have generated on every screen that asks for a page size of its own.
 */
function paged(path: string, page: number): string {
  const [route, existing = ""] = path.split("?");
  const parameters = new URLSearchParams(existing);

  parameters.set("page", String(page));

  if (!parameters.has("pageSize")) {
    parameters.set("pageSize", String(MAX_PAGE_SIZE));
  }

  return `${route}?${parameters.toString()}`;
}

/** Whether the caller's own path already sets a parameter. */
function asked(path: string, parameter: string): boolean {
  const [, existing = ""] = path.split("?");

  return new URLSearchParams(existing).has(parameter);
}

export function useResource<T>(path: string, enabled = true): UseQueryResult<T, Error> {
  return useQuery({
    queryKey: ["resource", path],
    queryFn: () => apiRequest<T>(path),
    enabled,
  });
}

/**
 * Performs a write and refreshes what it invalidated.
 *
 * Every action screen needs the same three things — send the request, refetch
 * the lists that just went stale, and surface the failure rather than leaving a
 * button that silently did nothing. Doing that here means no screen can forget
 * the third one.
 *
 * The request is derived from the input rather than fixed at construction,
 * because most actions carry something the operator typed: a mitigation note, a
 * root cause, the version a fix went out in.
 */
export function useAction<TResult = unknown, TInput = void>(
  request: (input: TInput) => { path: string; options?: RequestOptions },
  invalidate: string[] = [],
): UseMutationResult<TResult, Error, TInput> {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (input: TInput) => {
      const { path, options } = request(input);

      return apiRequest<TResult>(path, { method: "POST", ...options });
    },
    onSuccess: async () => {
      // Refetch rather than patch the cache: the server decides what a status
      // becomes — resolving a group that has since recurred reopens it — and a
      // locally guessed value would show the operator something untrue.
      await Promise.all(
        invalidate.map((prefix) =>
          client.invalidateQueries({
            predicate: (query) =>
              typeof query.queryKey[1] === "string" && query.queryKey[1].startsWith(prefix),
          }),
        ),
      );
    },
  });
}
