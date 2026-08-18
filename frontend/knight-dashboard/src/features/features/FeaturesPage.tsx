import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Plus, Upload, Ban, ShieldCheck, GitBranch, AlertTriangle } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import type { Feature, FeatureStatus, FeatureVersion, VersionStatus } from "@/lib/api/domain";
import { PageShell, PageHeader, Toolbar, FilterTabs, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatNumber } from "@/lib/utils/format";

const featureTone: Record<FeatureStatus, Tone> = {
  Published: "success",
  Draft: "info",
  Deprecated: "warning",
  Withdrawn: "neutral",
};

const versionTone: Record<VersionStatus, Tone> = {
  Published: "success",
  Draft: "info",
  Yanked: "danger",
};

type Filter = "all" | FeatureStatus;

/**
 * The feature registry: a catalogue of versioned, deployable packages.
 * Entitlement counts sit next to installation counts so the two facts stay
 * visibly distinct (docs/feature-delivery.md section 2).
 */
export function FeaturesPage() {
  const { t } = useTranslation();
  const query = useCollection<Feature>("/features");
  const can = useAuthStore((state) => state.can);
  const [filter, setFilter] = useState<Filter>("all");
  const [selected, setSelected] = useState<Feature | null>(null);

  const versions = useCollection<FeatureVersion>(
    selected ? `/features/${selected.id}/versions` : "/features/none/versions",
  );

  const all = query.data ?? [];
  const rows = all.filter((feature) => filter === "all" || feature.status === filter);

  const columns: Column<Feature>[] = [
    {
      key: "name",
      header: t("features.name"),
      render: (row) => (
        <span className="flex flex-col">
          <span className="font-medium text-on-surface">{row.name}</span>
          <Mono>{row.slug}</Mono>
        </span>
      ),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={featureTone[row.status]}>{t(`featureStatus.${row.status}`)}</StatusChip>
      ),
    },
    {
      key: "version",
      header: t("features.latestVersion"),
      mono: true,
      render: (row) => row.latestVersion ?? "—",
    },
    {
      key: "entitled",
      header: t("features.entitled"),
      numeric: true,
      render: (row) => formatNumber(row.entitledCount),
    },
    {
      key: "installed",
      header: t("features.installed"),
      numeric: true,
      render: (row) => (
        <span className={row.installCount < row.entitledCount ? "text-warning" : undefined}>
          {formatNumber(row.installCount)}
        </span>
      ),
    },
    {
      key: "plans",
      header: t("features.plans"),
      secondary: true,
      render: (row) =>
        row.plans.length === 0 ? "—" : row.plans.map((plan) => t(`planKey.${plan}`)).join("، "),
    },
  ];

  return (
    <PageShell>
      <PageHeader
        title={t("nav.features")}
        subtitle={t("features.subtitle")}
        actions={
          can("feature.manage") ? (
            <Button size="sm">
              <Plus className="size-4" aria-hidden />
              {t("features.create")}
            </Button>
          ) : undefined
        }
      />

      <CollectionCard
        query={query}
        toolbar={
          <Toolbar>
            <FilterTabs<Filter>
              value={filter}
              onChange={setFilter}
              options={[
                { value: "all", label: t("common.all"), count: all.length },
                {
                  value: "Published",
                  label: t("featureStatus.Published"),
                  count: all.filter((f) => f.status === "Published").length,
                },
                {
                  value: "Draft",
                  label: t("featureStatus.Draft"),
                  count: all.filter((f) => f.status === "Draft").length,
                },
                {
                  value: "Deprecated",
                  label: t("featureStatus.Deprecated"),
                  count: all.filter((f) => f.status === "Deprecated").length,
                },
              ]}
            />
          </Toolbar>
        }
      >
        {() => (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.id}
            onRowClick={setSelected}
            cardTitle={(row) => row.name}
            emptyMessage={t("common.noResults")}
          />
        )}
      </CollectionCard>

      <Drawer
        open={selected !== null}
        title={selected?.name ?? ""}
        subtitle={selected?.slug}
        onClose={() => setSelected(null)}
        footer={
          can("feature.publish") ? (
            <>
              <Button variant="outline" size="sm">
                <Ban className="size-4" aria-hidden />
                {t("features.yank")}
              </Button>
              <Button size="sm">
                <Upload className="size-4" aria-hidden />
                {t("features.publish")}
              </Button>
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-6">
            <p className="text-body-sm text-on-surface-variant">{selected.description}</p>

            {can("feature.publish") ? (
              <p className="flex items-start gap-2 rounded-md bg-warning/10 px-3 py-2.5 text-body-sm text-warning">
                <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden />
                {t("features.publishWarning")}
              </p>
            ) : null}

            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("common.status")}>
                <StatusChip tone={featureTone[selected.status]}>
                  {t(`featureStatus.${selected.status}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("features.optional")}>
                {selected.isOptional ? t("common.yes") : t("common.no")}
              </KeyValue>
              <KeyValue label={t("features.dedicatedOnly")}>
                {selected.requiresDedicatedInfrastructure ? t("common.yes") : t("common.no")}
              </KeyValue>
              <KeyValue label={t("features.entitled")}>
                {formatNumber(selected.entitledCount)}
              </KeyValue>
              <KeyValue label={t("features.installed")}>
                {formatNumber(selected.installCount)}
              </KeyValue>
            </dl>

            <section>
              <h3 className="label-caps mb-3 flex items-center gap-2 text-on-surface-variant/80">
                <GitBranch className="size-4" aria-hidden />
                {t("features.versions")}
              </h3>
              {versions.isPending ? (
                <p className="text-body-sm text-on-surface-variant">{t("common.loading")}</p>
              ) : (versions.data ?? []).length === 0 ? (
                <p className="text-body-sm text-on-surface-variant">{t("features.noVersions")}</p>
              ) : (
                <ul className="flex flex-col gap-3">
                  {(versions.data ?? []).map((version) => (
                    <li key={version.id} className="rounded-md bg-surface-low p-3.5">
                      <div className="flex items-center justify-between gap-2">
                        <span dir="ltr" className="font-mono text-body-sm text-on-surface">
                          {version.version}
                        </span>
                        <StatusChip tone={versionTone[version.status]}>
                          {t(`versionStatus.${version.status}`)}
                        </StatusChip>
                      </div>
                      <dl className="mt-2 flex flex-col gap-1 text-body-sm text-on-surface-variant">
                        <div className="flex items-center gap-2">
                          <ShieldCheck
                            className={`size-4 ${version.signed ? "text-success" : "text-error"}`}
                            aria-hidden
                          />
                          <Mono>{version.artifactDigest}</Mono>
                        </div>
                        <div>
                          {t("features.compatibleWith")} <Mono>{version.storeVersionRange}</Mono>
                        </div>
                        <div>
                          {t("features.migrations")}:{" "}
                          {version.migrations.required
                            ? version.migrations.reversible
                              ? t("features.reversible")
                              : t("features.irreversible")
                            : t("common.no")}
                        </div>
                        {version.dependencies.length > 0 ? (
                          <div>
                            {t("features.dependencies")}:{" "}
                            {version.dependencies.map((dependency) => (
                              <Mono key={dependency.slug} className="ms-1">
                                {dependency.slug} {dependency.range}
                              </Mono>
                            ))}
                          </div>
                        ) : null}
                        {version.publishedAt ? (
                          <div>
                            {t("features.publishedAt")}: {formatDateTime(version.publishedAt)} ·{" "}
                            {version.publishedBy}
                          </div>
                        ) : null}
                      </dl>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
