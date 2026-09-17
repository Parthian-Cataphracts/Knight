import { useState } from "react";
import { Link, useLocation } from "react-router-dom";
import { HelpCircle, X, AlertTriangle } from "lucide-react";
import { helpForPath } from "./helpContent";

/**
 * A header "?" button that opens a side panel with the plain-language guide for
 * the screen the operator is currently on. The full index lives at /help.
 */
export function HelpButton() {
  const [open, setOpen] = useState(false);
  const { pathname } = useLocation();
  const entry = helpForPath(pathname);

  return (
    <>
      <button
        type="button"
        className="grid size-10 place-items-center rounded-md text-on-surface-variant hover:bg-surface-high"
        aria-label="راهنما"
        title="راهنمای این صفحه"
        onClick={() => setOpen(true)}
      >
        <HelpCircle className="size-5" />
      </button>

      {open && (
        <div className="fixed inset-0 z-50 flex" role="dialog" aria-modal="true" aria-label="راهنما">
          <div className="flex-1 bg-black/40" onClick={() => setOpen(false)} />
          <aside
            dir="rtl"
            className="flex h-full w-full max-w-md flex-col overflow-y-auto bg-surface-lowest shadow-xl"
          >
            <div className="sticky top-0 flex items-center justify-between border-b border-outline-variant bg-surface-lowest px-5 py-4">
              <span className="flex items-center gap-2 text-body font-semibold text-on-surface">
                <HelpCircle className="size-5 text-primary" />
                راهنمای این صفحه
              </span>
              <button
                type="button"
                className="grid size-9 place-items-center rounded-md text-on-surface-variant hover:bg-surface-high"
                aria-label="بستن"
                onClick={() => setOpen(false)}
              >
                <X className="size-5" />
              </button>
            </div>

            <div className="flex-1 px-5 py-5">
              {entry ? (
                <>
                  <h2 className="text-title font-semibold text-on-surface">{entry.title}</h2>
                  <p className="mt-2 text-body-sm leading-7 text-on-surface-variant">{entry.what}</p>

                  <h3 className="mt-5 text-body-sm font-semibold text-on-surface">قدم‌به‌قدم</h3>
                  <ol className="mt-2 list-decimal space-y-2 ps-5 text-body-sm leading-7 text-on-surface-variant">
                    {entry.steps.map((s, i) => (
                      <li key={i}>{s}</li>
                    ))}
                  </ol>

                  {entry.caution && (
                    <p className="mt-5 flex items-start gap-2 rounded-md bg-error/10 p-3 text-body-sm leading-7 text-error">
                      <AlertTriangle className="mt-0.5 size-4 shrink-0" />
                      <span>{entry.caution}</span>
                    </p>
                  )}
                </>
              ) : (
                <p className="text-body-sm text-on-surface-variant">
                  برای این صفحه راهنمای اختصاصی ثبت نشده. فهرست کامل راهنماها را ببینید.
                </p>
              )}

              <Link
                to="/help"
                onClick={() => setOpen(false)}
                className="mt-6 inline-flex rounded-md bg-primary px-4 py-2 text-body-sm font-medium text-on-primary hover:opacity-90"
              >
                دیدن همهٔ راهنماها
              </Link>
            </div>
          </aside>
        </div>
      )}
    </>
  );
}
