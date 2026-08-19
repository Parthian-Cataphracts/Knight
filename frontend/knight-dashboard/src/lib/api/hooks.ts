import {
  useMutation,
  useQueryClient,
  useQuery,
  type UseMutationResult,
  type UseQueryResult,
} from "@tanstack/react-query";
import { apiRequest, type RequestOptions } from "./client";
import type { Paged } from "./types";

/** Reads a paged collection endpoint. Keys are the path, so caches stay distinct. */
export function useCollection<T>(path: string): UseQueryResult<T[], Error> {
  return useQuery({
    queryKey: ["collection", path],
    queryFn: async () => {
      const response = await apiRequest<Paged<T>>(path);
      return response.items;
    },
  });
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
