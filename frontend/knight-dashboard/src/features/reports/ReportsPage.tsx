import { useTranslation } from "react-i18next";
import { BarChart3, ChevronLeft } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import { PageShell, PageHeader } from "@/components/data/PageShell";
import { Card } from "@/components/ui/Card";
import { LoadingBlock, ErrorBlock } from "@/components/ui/StateBlock";
import { formatRelative } from "@/lib/utils/format";

interface Report {
  key: string;
  name: string;
  description: string;
  updatedAt: string;
}

export function ReportsPage() {
  const { t } = useTranslation();
  const query = useCollection<Report>("/reports");

  return (
    <PageShell>
      <PageHeader title={t("nav.reports")} subtitle={t("reports.subtitle")} />

      {query.isPending ? (
        <Card>
          <LoadingBlock rows={4} />
        </Card>
      ) : query.isError ? (
        <Card>
          <ErrorBlock message={query.error.message} onRetry={() => void query.refetch()} />
        </Card>
      ) : (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
          {query.data.map((report) => (
            <Card key={report.key} className="p-5">
              <div className="flex items-start gap-3">
                <span className="grid size-10 shrink-0 place-items-center rounded-md bg-surface-high text-primary">
                  <BarChart3 className="size-5" aria-hidden />
                </span>
                <div className="min-w-0 flex-1">
                  <h2 className="text-body font-medium text-on-surface">{report.name}</h2>
                  <p className="mt-1 text-body-sm text-on-surface-variant">{report.description}</p>
                  <p className="mt-3 flex items-center gap-1 text-body-sm text-on-surface-variant">
                    {t("reports.updated")} {formatRelative(report.updatedAt)}
                    <ChevronLeft className="size-4 rtl:-scale-x-100" aria-hidden />
                  </p>
                </div>
              </div>
            </Card>
          ))}
        </div>
      )}
    </PageShell>
  );
}
