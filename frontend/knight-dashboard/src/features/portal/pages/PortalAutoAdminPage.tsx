import { useMemo, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { Sparkles, Send, Check, X, ArrowRight, Bot, ShieldCheck, Zap } from "lucide-react";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";
import { StatusChip } from "@/components/ui/StatusChip";
import { LoadingBlock, ErrorBlock } from "@/components/ui/StateBlock";
import { formatDateTime } from "@/lib/utils/format";
import { ApiError } from "@/lib/api/problem";
import { cn } from "@/lib/utils/cn";
import { ButtonLink } from "../components";
import { formatMoney } from "../money";
import { usePublicPlans, useMySubscription, type PublicOptionalFeature } from "../api";
import {
  useAutoAdminSettings,
  useAutoAdminRuns,
  useSetAutonomy,
  useSubmitRun,
  useApproveRun,
  autoAdminRunsKey,
  autoAdminSettingsKey,
  type Autonomy,
  type ContentRun,
} from "../autoAdmin";

/** The generation parts, by slug — the ones that make the "give it a topic" run possible. */
const GENERATION_SLUGS = new Set([
  "auto-admin-image",
  "auto-admin-caption",
  "auto-admin-story",
  "auto-admin-video",
]);

/**
 * The customer's Automatic Admin (docs/adr/0038): set how autonomous it is, give
 * it a topic, and it generates content for the parts they bought and — on
 * approval, or straight away on full-auto — publishes to the channels they
 * connected, reporting back. Gated per Phase 32B: the engine only appears once the
 * customer owns at least one part; before that the page is the storefront for it.
 */
export function PortalAutoAdminPage() {
  const { t } = useTranslation();
  const settings = useAutoAdminSettings();
  const subscription = useMySubscription();
  const plans = usePublicPlans();

  if (settings.isLoading || subscription.isLoading || plans.isLoading) return <LoadingBlock rows={6} />;

  const failed = [settings, subscription, plans].find((q) => q.isError);
  if (failed?.isError) {
    const status = failed.error instanceof ApiError ? failed.error.status : undefined;
    const message = failed.error instanceof Error ? failed.error.message : String(failed.error);
    return <ErrorBlock message={message} status={status} onRetry={() => void failed.refetch()} />;
  }

  const customPlan = plans.data?.find((p) => p.key === "custom");
  const parts = (customPlan?.optionalFeatures ?? []).filter((f) => f.slug.startsWith("auto-admin-"));
  const entitled = new Set(subscription.data?.featureIds ?? []);
  const owned = parts.filter((p) => entitled.has(p.featureId));

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-start gap-3">
        <span className="grid size-11 place-items-center rounded-xl bg-primary/15 text-primary">
          <Bot className="size-6" aria-hidden />
        </span>
        <div>
          <h1 className="text-headline font-semibold text-on-surface">
            {t("portal.autoAdmin.title", "Automatic Admin")}
          </h1>
          <p className="mt-1 text-body-sm text-on-surface-variant">
            {t(
              "portal.autoAdmin.subtitle",
              "A virtual admin that creates content, publishes it to your channels and reports back for approval.",
            )}
          </p>
        </div>
      </div>

      {owned.length > 0 ? (
        <Engine settings={settings.data!} owned={owned} />
      ) : (
        <Storefront parts={parts} />
      )}

      {parts.length > 0 ? <PartsCatalogue parts={parts} owned={owned} /> : null}
    </div>
  );
}

/** The engine, shown once the customer owns at least one part. */
function Engine({ settings, owned }: { settings: { autonomy: Autonomy }; owned: PublicOptionalFeature[] }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const setAutonomy = useSetAutonomy();
  const submit = useSubmitRun();
  const runs = useAutoAdminRuns();

  const [topic, setTopic] = useState("");
  const [autonomy, setAutonomyState] = useState<Autonomy>(settings.autonomy);

  const canGenerate = owned.some((p) => GENERATION_SLUGS.has(p.slug));

  const chooseAutonomy = (value: Autonomy) => {
    setAutonomyState(value);
    setAutonomy.mutate(value, {
      onSuccess: () => queryClient.invalidateQueries({ queryKey: autoAdminSettingsKey }),
    });
  };

  const onGenerate = () => {
    const trimmed = topic.trim();
    if (!trimmed) return;
    submit.mutate(trimmed, {
      onSuccess: () => {
        setTopic("");
        void queryClient.invalidateQueries({ queryKey: autoAdminRunsKey });
      },
    });
  };

  return (
    <div className="flex flex-col gap-6">
      <Card>
        <CardHeader title={t("portal.autoAdmin.autonomyTitle", "How it works")} />
        <CardBody className="grid gap-3 sm:grid-cols-2">
          <AutonomyChoice
            active={autonomy === "ApprovalRequired"}
            onSelect={() => chooseAutonomy("ApprovalRequired")}
            icon={<ShieldCheck className="size-5" aria-hidden />}
            title={t("portal.autoAdmin.approvalTitle", "Draft, then approve")}
            body={t(
              "portal.autoAdmin.approvalBody",
              "Everything is drafted and waits for your one-tap approval before it goes out.",
            )}
          />
          <AutonomyChoice
            active={autonomy === "FullyAutomatic"}
            onSelect={() => chooseAutonomy("FullyAutomatic")}
            icon={<Zap className="size-5" aria-hidden />}
            title={t("portal.autoAdmin.autoTitle", "Fully automatic")}
            body={t(
              "portal.autoAdmin.autoBody",
              "Content is generated and published straight away, with no wait — like a real admin.",
            )}
          />
        </CardBody>
      </Card>

      <Card>
        <CardHeader title={t("portal.autoAdmin.runTitle", "Give it a topic")} />
        <CardBody className="flex flex-col gap-3">
          <textarea
            value={topic}
            onChange={(e) => setTopic(e.target.value)}
            rows={2}
            maxLength={500}
            disabled={!canGenerate}
            placeholder={t("portal.autoAdmin.topicPlaceholder", "e.g. Yalda sale on all rugs")}
            className="w-full resize-y rounded-md border border-outline-variant bg-surface px-3 py-2 text-body-sm text-on-surface placeholder:text-on-surface-variant focus:border-primary focus:outline-none disabled:opacity-60"
          />
          <div className="flex items-center justify-between gap-3">
            <p className="text-body-sm text-on-surface-variant">
              {canGenerate
                ? t("portal.autoAdmin.runHint", "It will make content for the parts you own and publish to your channels.")
                : t("portal.autoAdmin.noGenerationHint", "Add a content capability (image, caption, story or video) to run the admin.")}
            </p>
            <Button onClick={onGenerate} loading={submit.isPending} disabled={!canGenerate || topic.trim().length === 0}>
              <Send className="size-4 rtl:-scale-x-100" aria-hidden />
              {t("portal.autoAdmin.generate", "Generate")}
            </Button>
          </div>
          {submit.isError ? (
            <p role="alert" className="rounded-md bg-error/15 px-3 py-2 text-body-sm text-error">
              {submit.error instanceof Error ? submit.error.message : t("common.errorTitle", "Something went wrong")}
            </p>
          ) : null}
        </CardBody>
      </Card>

      <RunHistory runs={runs} />
    </div>
  );
}

function AutonomyChoice({
  active,
  onSelect,
  icon,
  title,
  body,
}: {
  active: boolean;
  onSelect: () => void;
  icon: ReactNode;
  title: string;
  body: string;
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      className={cn(
        "flex flex-col items-start gap-2 rounded-lg border p-4 text-start transition-colors",
        active ? "border-primary bg-primary/5 ring-1 ring-primary" : "border-outline-variant hover:bg-surface-high",
      )}
    >
      <span className={cn("grid size-9 place-items-center rounded-lg", active ? "bg-primary/15 text-primary" : "bg-surface-high text-on-surface-variant")}>
        {icon}
      </span>
      <span className="text-body font-medium text-on-surface">{title}</span>
      <span className="text-body-sm text-on-surface-variant">{body}</span>
    </button>
  );
}

function RunHistory({ runs }: { runs: ReturnType<typeof useAutoAdminRuns> }) {
  const { t } = useTranslation();
  if (runs.isLoading) return <LoadingBlock rows={3} />;
  const list = runs.data ?? [];
  if (list.length === 0) {
    return (
      <Card>
        <CardBody className="flex items-center gap-3 text-on-surface-variant">
          <Sparkles className="size-5" aria-hidden />
          <p className="text-body-sm">{t("portal.autoAdmin.noRuns", "No runs yet. Give the admin a topic above.")}</p>
        </CardBody>
      </Card>
    );
  }
  return (
    <div className="flex flex-col gap-3">
      <h2 className="text-title font-semibold text-on-surface">{t("portal.autoAdmin.runsTitle", "Recent runs")}</h2>
      {list.map((run) => (
        <RunCard key={run.id} run={run} />
      ))}
    </div>
  );
}

function RunCard({ run }: { run: ContentRun }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const approve = useApproveRun();

  const tone = run.status === "Published" ? (run.hasPublicationErrors ? "warning" : "success") : run.status === "Failed" ? "danger" : "info";

  const onApprove = () =>
    approve.mutate(run.id, { onSuccess: () => void queryClient.invalidateQueries({ queryKey: autoAdminRunsKey }) });

  return (
    <Card>
      <CardBody className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-body font-medium text-on-surface">{run.topic}</p>
          <StatusChip tone={tone}>{t(`portal.autoAdmin.status.${run.status}`, run.status)}</StatusChip>
        </div>

        {run.drafts.length > 0 ? (
          <ul className="flex flex-col gap-1.5">
            {run.drafts.map((d) => (
              <li key={d.kind} className="flex items-start gap-2 text-body-sm">
                <span className="mt-0.5 shrink-0 rounded bg-surface-high px-1.5 py-0.5 text-label text-on-surface-variant">
                  {t(`portal.autoAdmin.kind.${d.kind}`, d.kind)}
                </span>
                <span className="text-on-surface">{d.body}</span>
              </li>
            ))}
          </ul>
        ) : null}

        {run.publications.length > 0 ? (
          <div className="flex flex-wrap gap-2">
            {run.publications.map((p) => (
              <span
                key={p.channelKey}
                className={cn(
                  "inline-flex items-center gap-1 rounded-md px-2 py-1 text-body-sm",
                  p.succeeded ? "bg-success/15 text-success" : "bg-error/15 text-error",
                )}
                title={p.detail}
              >
                {p.succeeded ? <Check className="size-3.5" aria-hidden /> : <X className="size-3.5" aria-hidden />}
                {p.channelKey}
              </span>
            ))}
          </div>
        ) : null}

        <div className="flex items-center justify-between gap-3">
          <span className="text-body-sm text-on-surface-variant">{formatDateTime(run.createdAt)}</span>
          {run.status === "Draft" ? (
            <Button size="sm" onClick={onApprove} loading={approve.isPending}>
              {t("portal.autoAdmin.approve", "Approve & publish")}
              <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
            </Button>
          ) : null}
        </div>
      </CardBody>
    </Card>
  );
}

/** Shown when the customer owns no parts yet: the pitch and a way to buy. */
function Storefront({ parts }: { parts: PublicOptionalFeature[] }) {
  const { t } = useTranslation();
  return (
    <Card>
      <CardBody className="flex flex-col items-start gap-4">
        <span className="grid size-12 place-items-center rounded-xl bg-primary/15 text-primary">
          <Sparkles className="size-6" aria-hidden />
        </span>
        <div>
          <h2 className="text-title font-semibold text-on-surface">
            {t("portal.autoAdmin.pitchTitle", "Hire a virtual admin")}
          </h2>
          <p className="mt-1 text-body-sm text-on-surface-variant">
            {t(
              "portal.autoAdmin.pitchBody",
              "Pick the channels and the content you need. You pay only for the parts you switch on.",
            )}
          </p>
        </div>
        {parts.length > 0 ? (
          <ButtonLink to="/portal/plans">
            {t("portal.autoAdmin.choose", "Choose capabilities")}
            <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
          </ButtonLink>
        ) : null}
      </CardBody>
    </Card>
  );
}

/** The parts and their prices, with a live total of the ones the customer picks. */
function PartsCatalogue({ parts, owned }: { parts: PublicOptionalFeature[]; owned: PublicOptionalFeature[] }) {
  const { t } = useTranslation();
  const ownedIds = new Set(owned.map((p) => p.featureId));
  const [selected, setSelected] = useState<Set<string>>(new Set());

  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const currency = parts[0]?.currency ?? "EUR";
  const total = useMemo(
    () => parts.filter((p) => selected.has(p.featureId)).reduce((sum, p) => sum + (p.price ?? 0), 0),
    [parts, selected],
  );

  return (
    <Card>
      <CardHeader title={t("portal.autoAdmin.catalogueTitle", "Capabilities")} />
      <CardBody className="flex flex-col gap-3">
        <div className="flex flex-col gap-2">
          {parts.map((p) => {
            const isOwned = ownedIds.has(p.featureId);
            return (
              <label
                key={p.featureId}
                className={cn(
                  "flex cursor-pointer items-center justify-between gap-3 rounded-md border px-3 py-2.5",
                  isOwned ? "border-success/40 bg-success/5" : "border-outline-variant hover:bg-surface-high",
                )}
              >
                <span className="flex items-center gap-2.5">
                  {isOwned ? (
                    <Check className="size-4 shrink-0 text-success" aria-hidden />
                  ) : (
                    <input
                      type="checkbox"
                      className="size-4 rounded-sm accent-[var(--primary)]"
                      checked={selected.has(p.featureId)}
                      onChange={() => toggle(p.featureId)}
                    />
                  )}
                  <span>
                    <span className="block text-body-sm font-medium text-on-surface">{p.name}</span>
                    {p.description ? <span className="block text-body-sm text-on-surface-variant">{p.description}</span> : null}
                  </span>
                </span>
                <span className="shrink-0 text-body-sm font-medium text-on-surface">
                  {isOwned
                    ? t("portal.autoAdmin.owned", "Active")
                    : p.price === null
                      ? "—"
                      : `+${formatMoney(p.price, p.currency)}`}
                </span>
              </label>
            );
          })}
        </div>

        {selected.size > 0 ? (
          <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-surface-low px-4 py-3">
            <div>
              <p className="text-body-sm text-on-surface-variant">{t("portal.autoAdmin.selectionTotal", "Selected")}</p>
              <p className="text-title font-semibold text-on-surface">{formatMoney(total, currency)}</p>
            </div>
            <ButtonLink to="/portal/plans">
              {t("portal.autoAdmin.addToPlan", "Add to my plan")}
              <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
            </ButtonLink>
          </div>
        ) : null}
      </CardBody>
    </Card>
  );
}
