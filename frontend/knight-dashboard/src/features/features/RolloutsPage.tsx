import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Rocket, TriangleAlert } from "lucide-react";
import { useAction, useCollection } from "@/lib/api/hooks";
import type { Rollout, RolloutState, RolloutTargetState, RolloutWave } from "@/lib/api/domain";
import { PageShell, PageHeader, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { useAuthStore } from "@/store/auth";
import { formatDateTime } from "@/lib/utils/format";

const rolloutTone: Record<RolloutState, Tone> = {
  Planned: "neutral",
  InProgress: "info",
  Halted: "danger",
  Completed: "success",
  Cancelled: "neutral",
};

const targetTone: Record<RolloutTargetState, Tone> = {
  Pending: "neutral",
  Dispatched: "info",
  Succeeded: "success",
  Failed: "danger",
};

/**
 * Staged rollouts: moving the fleet onto one Feature version, a wave at a time
 * (docs/adr/0028-staged-rollouts-with-a-single-store-canary.md).
 *
 * Two things this screen must not do, both of which would undermine the
 * mitigation it exists to operate:
 *
 * - It must never hide `haltReason`. A halted rollout and one that is merely
 *   between waves look identical from the progress numbers alone, and the whole
 *   point of halting is that somebody comes and reads why.
 * - It must show the canary as the canary. An operator confirming a rollout is
 *   agreeing to send unproven code to a named store first, and that store's name
 *   is the most important thing on the page.
 *
 * Planning is deliberately a separate step from starting. `POST /rollouts` sends
 * nothing to any store; it answers with the waves so they can be read before
 * anyone commits.
 */
export function RolloutsPage() {
  const { t } = useTranslation();
  const can = useAuthStore((state) => state.can);
  const mayRollOut = can("feature.publish");

  const rollouts = useCollection<Rollout>("/rollouts", mayRollOut);

  const [slug, setSlug] = useState("");
  const [version, setVersion] = useState("");
  const [percentages, setPercentages] = useState("50, 100");
  const [threshold, setThreshold] = useState("1");

  const plan = useAction<Rollout, void>(
    () => ({
      path: "/rollouts",
      options: {
        body: {
          slug: slug.trim(),
          version: version.trim(),
          wavePercentages: percentages
            .split(",")
            .map((part) => Number.parseInt(part.trim(), 10))
            .filter((value) => Number.isFinite(value) && value > 0),
          failureThreshold: Number.parseInt(threshold, 10) || 1,
        },
      },
    }),
    ["/rollouts"],
  );

  const start = useAction<Rollout, string>((id) => ({ path: `/rollouts/${id}/start` }), ["/rollouts"]);
  const resume = useAction<Rollout, string>((id) => ({ path: `/rollouts/${id}/resume` }), ["/rollouts"]);

  const halt = useAction<Rollout, string>(
    (id) => ({ path: `/rollouts/${id}/halt`, options: { body: { reason: t("rollouts.haltedByOperator") } } }),
    ["/rollouts"],
  );

  const cancel = useAction<Rollout, string>(
    (id) => ({ path: `/rollouts/${id}/cancel`, options: { body: { reason: t("rollouts.cancelledByOperator") } } }),
    ["/rollouts"],
  );

  // A screen for a permission the signed-in user does not hold says so plainly
  // rather than showing an empty table that looks like "no rollouts yet".
  if (!mayRollOut) {
    return (
      <PageShell>
        <PageHeader title={t("rollouts.title")} subtitle={t("rollouts.subtitle")} />
        <Card>
          <CardBody>
            <p className="text-body-sm text-on-surface-variant">{t("rollouts.notPermitted")}</p>
          </CardBody>
        </Card>
      </PageShell>
    );
  }

  return (
    <PageShell>
      <PageHeader title={t("rollouts.title")} subtitle={t("rollouts.subtitle")} />

      <Card>
        <CardHeader title={t("rollouts.plan")} icon={<Rocket className="size-5" />} />
        <CardBody className="flex flex-col gap-4">
          <p className="text-body-sm text-on-surface-variant">{t("rollouts.planningNote")}</p>

          <div className="grid gap-4 md:grid-cols-2">
            <TextField
              label={t("rollouts.slug")}
              value={slug}
              dir="ltr"
              placeholder="knight-feature-promotions"
              onChange={(event) => setSlug(event.target.value)}
            />
            <TextField
              label={t("rollouts.version")}
              value={version}
              dir="ltr"
              placeholder="1.1.0"
              onChange={(event) => setVersion(event.target.value)}
            />
            <TextField
              label={t("rollouts.wavePercentages")}
              value={percentages}
              dir="ltr"
              placeholder="50, 100"
              onChange={(event) => setPercentages(event.target.value)}
            />
            <TextField
              label={t("rollouts.failureThreshold")}
              value={threshold}
              dir="ltr"
              onChange={(event) => setThreshold(event.target.value)}
            />
          </div>

          <div>
            <Button
              disabled={slug.trim() === "" || version.trim() === "" || plan.isPending}
              onClick={() => plan.mutate()}
            >
              {t("rollouts.planAction")}
            </Button>
          </div>

          {plan.isError ? <p className="text-body-sm text-error">{plan.error.message}</p> : null}
        </CardBody>
      </Card>

      <CollectionCard query={rollouts}>
        {(rows) =>
          rows.length === 0 ? (
            <p className="text-body-sm text-on-surface-variant">{t("rollouts.none")}</p>
          ) : (
            <div className="flex flex-col gap-4">
              {rows.map((rollout) => (
                <Card key={rollout.id}>
                  <CardHeader
                    title={`${rollout.featureSlug} → ${rollout.targetVersion}`}
                    action={<StatusChip tone={rolloutTone[rollout.state]}>{t(`rolloutState.${rollout.state}`)}</StatusChip>}
                  />
                  <CardBody className="flex flex-col gap-4">
                    <p className="text-body-sm text-on-surface-variant">
                      {t("rollouts.progress", {
                        succeeded: rollout.succeededStores,
                        failed: rollout.failedStores,
                        total: rollout.totalStores,
                      })}{" "}
                      · {t("rollouts.thresholdIs", { count: rollout.failureThreshold })} ·{" "}
                      {formatDateTime(rollout.createdAt)}
                    </p>

                    {/* Never hidden. See the note at the top of this file. */}
                    {rollout.haltReason ? (
                      <p className="flex items-start gap-2 text-body-sm text-error">
                        <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
                        <span>{rollout.haltReason}</span>
                      </p>
                    ) : null}

                    <div className="flex flex-col gap-3">
                      {rollout.waves.map((wave) => (
                        <WaveRow key={wave.id} wave={wave} />
                      ))}
                    </div>

                    <div className="flex flex-wrap gap-2">
                      {rollout.state === "Planned" ? (
                        <Button size="sm" onClick={() => start.mutate(rollout.id)}>
                          {t("rollouts.start")}
                        </Button>
                      ) : null}

                      {rollout.state === "InProgress" ? (
                        <Button size="sm" variant="outline" onClick={() => halt.mutate(rollout.id)}>
                          {t("rollouts.halt")}
                        </Button>
                      ) : null}

                      {rollout.state === "Halted" ? (
                        <Button size="sm" variant="outline" onClick={() => resume.mutate(rollout.id)}>
                          {t("rollouts.resume")}
                        </Button>
                      ) : null}

                      {rollout.state === "Planned" || rollout.state === "InProgress" || rollout.state === "Halted" ? (
                        <Button size="sm" variant="outline" onClick={() => cancel.mutate(rollout.id)}>
                          {t("rollouts.cancel")}
                        </Button>
                      ) : null}
                    </div>
                  </CardBody>
                </Card>
              ))}
            </div>
          )
        }
      </CollectionCard>
    </PageShell>
  );
}

function WaveRow({ wave }: { wave: RolloutWave }) {
  const { t } = useTranslation();

  return (
    <div className="rounded-md border border-outline-variant p-3">
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <span className="text-body-sm font-medium text-on-surface">
          {wave.isCanary ? t("rollouts.canaryWave") : t("rollouts.wave", { waveNumber: wave.ordinal })}
        </span>
        <StatusChip tone={wave.state === "Completed" ? "success" : wave.state === "Dispatched" ? "info" : "neutral"}>
          {t(`rolloutWaveState.${wave.state}`)}
        </StatusChip>
        <span className="text-body-sm text-on-surface-variant">
          {t("rollouts.storeCount", { count: wave.targets.length })}
        </span>
      </div>

      <div className="flex flex-col gap-1">
        {wave.targets.map((target) => (
          <div key={target.storeId} className="flex flex-wrap items-center gap-2 text-body-sm">
            <StatusChip tone={targetTone[target.state]}>{t(`rolloutTargetState.${target.state}`)}</StatusChip>
            <Mono>{target.storeId.slice(0, 8)}</Mono>
            {target.detail ? <span className="text-error">{target.detail}</span> : null}
          </div>
        ))}
      </div>
    </div>
  );
}
