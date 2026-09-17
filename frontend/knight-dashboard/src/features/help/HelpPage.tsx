import { Link } from "react-router-dom";
import { HelpCircle, ArrowLeft, AlertTriangle } from "lucide-react";
import { HELP_SECTIONS } from "./helpContent";

/**
 * The dedicated guide page: every KNIGHT area in one place, each linked to the
 * screen it describes. Reached from the sidebar and from every help panel.
 */
export function HelpPage() {
  return (
    <div dir="rtl" className="mx-auto max-w-3xl">
      <header className="mb-6">
        <h1 className="flex items-center gap-2 text-headline font-semibold text-on-surface">
          <HelpCircle className="size-6 text-primary" />
          راهنمای استفاده
        </h1>
        <p className="mt-2 text-body-sm leading-7 text-on-surface-variant">
          راهنمای ساده و کامل همهٔ بخش‌های پنل نایت. روی «رفتن به این بخش» بزنید تا
          مستقیم به همان صفحه بروید. راهنمای فیچرها و پنل فروشگاه در پوشهٔ
          <span className="mx-1 font-mono">docs/usage</span> مخزن است.
        </p>
      </header>

      <nav className="mb-8 flex flex-wrap gap-2">
        {HELP_SECTIONS.map((s) => (
          <a
            key={s.group}
            href={`#group-${s.group}`}
            className="rounded-full border border-outline-variant px-3 py-1 text-body-sm text-on-surface-variant hover:bg-surface-high"
          >
            {s.group}
          </a>
        ))}
      </nav>

      {HELP_SECTIONS.map((section) => (
        <section key={section.group} id={`group-${section.group}`} className="mb-10 scroll-mt-20">
          <h2 className="mb-4 border-b border-outline-variant pb-2 text-title font-semibold text-on-surface">
            {section.group}
          </h2>
          <div className="space-y-5">
            {section.entries.map((entry) => (
              <article
                key={entry.path}
                className="rounded-lg border border-outline-variant bg-surface-low p-4"
              >
                <div className="flex items-center justify-between gap-3">
                  <h3 className="text-body font-semibold text-on-surface">{entry.title}</h3>
                  <Link
                    to={entry.path}
                    className="inline-flex shrink-0 items-center gap-1 text-body-sm font-medium text-primary hover:underline"
                  >
                    رفتن به این بخش
                    <ArrowLeft className="size-4 rtl:-scale-x-100" />
                  </Link>
                </div>
                <p className="mt-2 text-body-sm leading-7 text-on-surface-variant">{entry.what}</p>
                <ol className="mt-3 list-decimal space-y-1.5 ps-5 text-body-sm leading-7 text-on-surface-variant">
                  {entry.steps.map((s, i) => (
                    <li key={i}>{s}</li>
                  ))}
                </ol>
                {entry.caution && (
                  <p className="mt-3 flex items-start gap-2 rounded-md bg-error/10 p-2.5 text-body-sm leading-7 text-error">
                    <AlertTriangle className="mt-0.5 size-4 shrink-0" />
                    <span>{entry.caution}</span>
                  </p>
                )}
              </article>
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}
