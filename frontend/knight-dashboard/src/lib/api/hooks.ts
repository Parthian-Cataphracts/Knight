import { useQuery, type UseQueryResult } from "@tanstack/react-query";
import { apiRequest } from "./client";
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
