import { useTranslation } from "react-i18next";
import { Construction } from "lucide-react";
import { Card, CardBody } from "@/components/ui/Card";

/** Routes that exist in the navigation but whose screens arrive in a later phase. */
export function PlaceholderPage({ titleKey }: { titleKey: string }) {
  const { t } = useTranslation();
  return (
    <div className="mx-auto flex w-full max-w-[1400px] flex-col gap-6">
      <h1 className="text-headline font-semibold text-on-surface">{t(titleKey)}</h1>
      <Card>
        <CardBody className="flex items-start gap-4">
          <span className="grid size-10 shrink-0 place-items-center rounded-md bg-surface-high text-on-surface-variant">
            <Construction className="size-5" aria-hidden />
          </span>
          <div>
            <p className="text-body text-on-surface">{t("common.comingSoon")}</p>
            <p className="mt-1 text-body-sm text-on-surface-variant">{t("common.comingSoonHint")}</p>
          </div>
        </CardBody>
      </Card>
    </div>
  );
}
