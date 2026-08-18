import type { ReactNode } from "react";
import { cn } from "@/lib/utils/cn";

export interface Column<T> {
  key: string;
  header: string;
  /** Numeric and identifier columns render end-aligned and monospaced. */
  numeric?: boolean;
  mono?: boolean;
  /** Hide on narrow desktops; the mobile card view always shows every column. */
  secondary?: boolean;
  render: (row: T) => ReactNode;
}

interface DataTableProps<T> {
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string;
  onRowClick?: (row: T) => void;
  /** Rendered as the card title on mobile; falls back to the first column. */
  cardTitle?: (row: T) => ReactNode;
  emptyMessage: string;
}

/**
 * Data-dense table for desktop that collapses to stacked cards below `md`,
 * per docs/frontend-architecture.md section 5. The table scrolls inside its own
 * container so the page never scrolls horizontally.
 */
export function DataTable<T>({
  columns,
  rows,
  rowKey,
  onRowClick,
  cardTitle,
  emptyMessage,
}: DataTableProps<T>) {
  if (rows.length === 0) {
    return <p className="p-5 text-body-sm text-on-surface-variant">{emptyMessage}</p>;
  }

  return (
    <>
      <div className="hidden overflow-x-auto md:block">
        <table className="w-full border-collapse text-body-sm">
          <thead>
            <tr className="border-b border-outline-variant">
              {columns.map((column) => (
                <th
                  key={column.key}
                  scope="col"
                  className={cn(
                    "label-caps whitespace-nowrap px-5 py-3 text-on-surface-variant/80",
                    column.numeric ? "text-end" : "text-start",
                    column.secondary && "hidden xl:table-cell",
                  )}
                >
                  {column.header}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr
                key={rowKey(row)}
                onClick={onRowClick ? () => onRowClick(row) : undefined}
                className={cn(
                  "border-b border-outline-variant/60 last:border-0",
                  onRowClick && "cursor-pointer hover:bg-surface-high",
                )}
              >
                {columns.map((column) => (
                  <td
                    key={column.key}
                    className={cn(
                      "px-5 py-3.5 align-middle text-on-surface",
                      column.numeric ? "text-end" : "text-start",
                      column.mono && "font-mono text-label",
                      column.secondary && "hidden xl:table-cell",
                    )}
                    dir={column.mono ? "ltr" : undefined}
                  >
                    {column.render(row)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <ul className="divide-y divide-outline-variant md:hidden">
        {rows.map((row) => {
          const [first, ...rest] = columns;
          return (
            <li key={rowKey(row)}>
              <div
                role={onRowClick ? "button" : undefined}
                tabIndex={onRowClick ? 0 : undefined}
                onClick={onRowClick ? () => onRowClick(row) : undefined}
                onKeyDown={
                  onRowClick
                    ? (event) => {
                        if (event.key === "Enter" || event.key === " ") {
                          event.preventDefault();
                          onRowClick(row);
                        }
                      }
                    : undefined
                }
                className={cn("flex flex-col gap-2.5 px-4 py-4", onRowClick && "active:bg-surface-high")}
              >
                <div className="text-body font-medium text-on-surface">
                  {cardTitle ? cardTitle(row) : first?.render(row)}
                </div>
                <dl className="flex flex-col gap-1.5">
                  {rest.map((column) => (
                    <div key={column.key} className="flex items-start justify-between gap-3">
                      <dt className="label-caps shrink-0 text-on-surface-variant/80">
                        {column.header}
                      </dt>
                      <dd
                        className={cn(
                          "min-w-0 text-end text-body-sm text-on-surface",
                          column.mono && "font-mono text-label",
                        )}
                        dir={column.mono ? "ltr" : undefined}
                      >
                        {column.render(row)}
                      </dd>
                    </div>
                  ))}
                </dl>
              </div>
            </li>
          );
        })}
      </ul>
    </>
  );
}
