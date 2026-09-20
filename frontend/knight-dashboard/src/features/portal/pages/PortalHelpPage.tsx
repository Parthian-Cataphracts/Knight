import { Link } from "react-router-dom";
import { HelpCircle, AlertTriangle, ArrowLeft } from "lucide-react";
import { Card, CardBody } from "@/components/ui/Card";
import { PORTAL_HELP_ENTRIES } from "../help/portalHelpContent";

/**
 * The portal's help index — every page's plain-language guide in one place, the
 * merchant's counterpart to the operations dashboard's /help. Each entry links
 * to the page it describes so the guide and the screen are one click apart.
 */
export function PortalHelpPage() {
  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-center gap-3">
        <span className="grid size-11 place-items-center rounded-lg bg-primary/15 text-primary">
          <HelpCircle className="size-6" aria-hidden />
        </span>
        <div>
          <h1 className="text-headline font-semibold text-on-surface">راهنمای پورتال</h1>
          <p className="mt-1 text-body-sm text-on-surface-variant">
            هر صفحه‌ی پنل شما، به زبان ساده و قدم‌به‌قدم.
          </p>
        </div>
      </div>

      {PORTAL_HELP_ENTRIES.map((entry) => (
        <Card key={entry.path}>
          <CardBody className="flex flex-col gap-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-title font-semibold text-on-surface">{entry.title}</h2>
              {entry.path !== "/portal/pay" ? (
                <Link
                  to={entry.path === "/portal/stores" ? "/portal" : entry.path}
                  className="inline-flex items-center gap-1 text-body-sm text-primary hover:underline"
                >
                  رفتن به این بخش
                  <ArrowLeft className="size-4 rtl:-scale-x-100" aria-hidden />
                </Link>
              ) : null}
            </div>
            <p className="text-body-sm leading-7 text-on-surface-variant">{entry.what}</p>
            <ol className="list-decimal space-y-1.5 ps-5 text-body-sm leading-7 text-on-surface-variant">
              {entry.steps.map((s, i) => (
                <li key={i}>{s}</li>
              ))}
            </ol>
            {entry.caution ? (
              <p className="flex items-start gap-2 rounded-md bg-error/10 p-3 text-body-sm leading-7 text-error">
                <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden />
                <span>{entry.caution}</span>
              </p>
            ) : null}
          </CardBody>
        </Card>
      ))}
    </div>
  );
}
