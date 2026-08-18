import type { ReactNode } from "react";
import type { UseQueryResult } from "@tanstack/react-query";
import { Card } from "@/components/ui/Card";
import { LoadingBlock, ErrorBlock } from "@/components/ui/StateBlock";
import { ApiError } from "@/lib/api/problem";

/**
 * Card wrapper that renders the loading, error and content states of a query
 * uniformly, so no screen re-implements them.
 */
export function CollectionCard<T>({
  query,
  toolbar,
  children,
}: {
  query: UseQueryResult<T, Error>;
  toolbar?: ReactNode;
  children: (data: T) => ReactNode;
}) {
  return (
    <Card className="overflow-hidden">
      {toolbar}
      {query.isPending ? (
        <LoadingBlock rows={5} />
      ) : query.isError ? (
        <ErrorBlock
          message={query.error.message}
          status={query.error instanceof ApiError ? query.error.status : undefined}
          onRetry={() => void query.refetch()}
        />
      ) : (
        children(query.data)
      )}
    </Card>
  );
}
