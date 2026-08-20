import { useState } from "react";
import { useParams, Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { ChevronLeft, KeyRound, RefreshCw, Ban, Globe } from "lucide-react";
import { useAction, useCollection } from "@/lib/api/hooks";
import type { Installation, Store } from "@/lib/api/domain";
import type { ActivityEntry, Deployment, StoreCredential, StoreDomain } from "@/lib/api/fixtures-detail";
import { PageShell, PageHeader, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Tabs, Timeline } from "@/components/data/Tabs";
import { AreaChart } from "@/components/data/Sparkline";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { EditDrawer } from "@/features/shared/EditDrawer";
import { LoadingBlock, ErrorBlock } from "@/components/ui/StateBlock";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatRelative } from "@/lib/utils/format";
import { installationTone } from "@/features/installations/installationTone";

type Tab = "overview" | "features" | "domains" | "credentials" | "deployments" | "activity";

/**
 * What the store detail page can honestly show.
 *
 * There is no request count and no storage figure because stores report
 * neither: a dashboard number that was estimated rather than measured is worse
 * than one fewer panel.
 */
interface UsageResponse {
  errors: number[];
  logs: number[];
  healthLatencyMs: number[];
  windowHours: number;
  totalErrors: number;
  totalLogs: number;
}

const verificationTone: Record<StoreDomain["verification"], Tone> = {
  Verified: "success",
  Pending: "warning",
  NotStarted: "neutral",
  Failed: "danger",
};

const credentialTone: Record<StoreCredential["state"], Tone> = {
  Active: "success",
  GracePeriod: "warning",
  Expired: "neutral",
  Revoked: "neutral",
};

export function StoreDetailPage() {
  const { t } = useTranslation();
  const { storeId = "" } = useParams();
  const can = useAuthStore((state) => state.can);

  const lifecycle = useAction<unknown, "activate" | "suspend" | "archive">(
    (action) => ({ path: `/stores/${storeId}/${action}` }),
    ["/stores"],
  );

  // A newly issued credential is the only moment its secret exists in a form
  // anyone can read: the API returns it once and stores a hash. So it is held
  // here to be shown, and never fetched again.
  const [issued, setIssued] = useState<{ clientId: string; clientSecret: string } | null>(null);
  const [editing, setEditing] = useState(false);

  const issueCredential = useAction<{ clientId: string; clientSecret: string }, void>(
    () => ({ path: `/stores/${storeId}/credentials` }),
    ["/stores"],
  );

  const startVerification = useAction<unknown, void>(
    () => ({ path: `/stores/${storeId}/domain-verification` }),
    ["/stores"],
  );

  const verifyDomain = useAction<unknown, void>(
    () => ({ path: `/stores/${storeId}/domain-verification/verify` }),
    ["/stores"],
  );
  const [tab, setTab] = useState<Tab>("overview");

  const stores = useCollection<Store>("/stores");
  const installations = useCollection<Installation>("/installations");
  const domains = useCollection<StoreDomain>(`/stores/${storeId}/domains`);
  const credentials = useCollection<StoreCredential>(`/stores/${storeId}/credentials`);
  const deployments = useCollection<Deployment>(`/stores/${storeId}/deployments`);
  const activity = useCollection<ActivityEntry>(`/stores/${storeId}/activity`);
  const usage = useCollection<UsageResponse>(`/stores/${storeId}/usage`);

  const store = (stores.data ?? []).find((item) => item.id === storeId);
  const storeInstallations = (installations.data ?? []).filter((item) => item.storeId === storeId);
  const usageData = usage.data?.[0];

  if (stores.isPending) {
    return (
      <PageShell>
        <Card>
          <LoadingBlock rows={6} />
        </Card>
      </PageShell>
    );
  }

  if (stores.isError || !store) {
    return (
      <PageShell>
        <Card>
          <ErrorBlock message={stores.error?.message ?? t("common.noResults")} />
        </Card>
      </PageShell>
    );
  }

  const installationColumns: Column<Installation>[] = [
    { key: "feature", header: t("installations.feature"), render: (row) => row.featureName },
    {
      key: "entitlement",
      header: t("installations.entitlement"),
      render: (row) => (
        <StatusChip tone={row.entitled ? "success" : "neutral"}>
          {row.entitled ? t("installations.entitled") : t("installations.notEntitled")}
        </StatusChip>
      ),
    },
    {
      key: "state",
      header: t("installations.installation"),
      render: (row) => (
        <StatusChip tone={installationTone[row.state]}>
          {t(`installationState.${row.state}`)}
        </StatusChip>
      ),
    },
    { key: "version", header: t("installations.version"), mono: true, render: (row) => row.installedVersion ?? "—" },
  ];

  const domainColumns: Column<StoreDomain>[] = [
    { key: "host", header: t("domains.host"), mono: true, render: (row) => row.host },
    { key: "type", header: t("domains.type"), render: (row) => t(`domainType.${row.type}`) },
    {
      key: "verification",
      header: t("domains.verification"),
      render: (row) => (
        <StatusChip tone={verificationTone[row.verification]}>
          {t(`domainVerification.${row.verification}`)}
        </StatusChip>
      ),
    },
    {
      key: "verifiedAt",
      header: t("domains.verifiedAt"),
      render: (row) => (row.verifiedAt ? formatRelative(row.verifiedAt) : "—"),
    },
  ];

  const credentialColumns: Column<StoreCredential>[] = [
    { key: "clientId", header: t("credentials.clientId"), mono: true, render: (row) => row.clientId },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={credentialTone[row.state]}>
          {t(`credentialStatus.${row.state}`)}
        </StatusChip>
      ),
    },
    { key: "created", header: t("credentials.createdAt"), render: (row) => formatDateTime(row.createdAt) },
    {
      key: "lastUsed",
      header: t("credentials.lastUsed"),
      render: (row) => (row.lastUsedAt ? formatRelative(row.lastUsedAt) : "—"),
    },
  ];

  const deploymentColumns: Column<Deployment>[] = [
    { key: "version", header: t("stores.version"), mono: true, render: (row) => row.version },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip
          tone={
            row.status === "Succeeded"
              ? "success"
              : row.status === "Detected"
                ? "neutral"
                : "danger"
          }
        >
          {t(`deploymentStatus.${row.status}`)}
        </StatusChip>
      ),
    },
    { key: "at", header: t("deployments.deployedAt"), render: (row) => formatRelative(row.deployedAt) },
    {
      key: "by",
      header: t("deployments.deployedBy"),
      render: (row) => row.deployedBy ?? (row.source === "StoreReported" ? t("deployments.reportedByStore") : t("deployments.detected")),
    },
    { key: "notes", header: t("deployments.notes"), secondary: true, render: (row) => row.notes ?? "—" },
  ];

  return (
    <PageShell>
      <Link
        to="/stores"
        className="flex w-fit items-center gap-1 text-body-sm text-on-surface-variant hover:text-on-surface"
      >
        <ChevronLeft className="size-4 rtl:-scale-x-100" aria-hidden />
        {t("nav.stores")}
      </Link>

      <PageHeader
        title={store.primaryDomain}
        subtitle={`${store.customerName} · ${t(`environment.${store.environment}`)} · ${t(`hosting.${store.hostingModel}`)}`}
        actions={
          can("store.manage") ? (
            <>
              {store.status === "Active" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={lifecycle.isPending}
                  onClick={() => lifecycle.mutate("suspend")}
                >
                  <Ban className="size-4" aria-hidden />
                  {t("stores.suspend")}
                </Button>
              ) : (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={lifecycle.isPending}
                  onClick={() => lifecycle.mutate("activate")}
                >
                  {t("stores.activate")}
                </Button>
              )}

              <Button variant="outline" size="sm" onClick={() => setEditing(true)}>
                {t("common.edit")}
              </Button>

              {store.status !== "Archived" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={lifecycle.isPending}
                  onClick={() => lifecycle.mutate("archive")}
                >
                  {t("stores.archive")}
                </Button>
              ) : null}
            </>
          ) : undefined
        }
      />

      <EditDrawer
        open={editing}
        title={t("stores.edit")}
        subtitle={store.primaryDomain}
        path={`/stores/${storeId}`}
        fields={[
          { key: "name", label: t("common.name"), value: store.name },
          {
            key: "primaryDomain",
            label: t("stores.primaryDomain"),
            value: store.primaryDomain,
            ltr: true,
          },
        ]}
        onClose={() => setEditing(false)}
        onSaved={() => {
          setEditing(false);
          void stores.refetch();
        }}
      />

      <Tabs<Tab>
        value={tab}
        onChange={setTab}
        options={[
          { value: "overview", label: t("storeDetail.overview") },
          { value: "features", label: t("storeDetail.features"), count: storeInstallations.length },
          { value: "domains", label: t("storeDetail.domains"), count: (domains.data ?? []).length },
          { value: "credentials", label: t("storeDetail.credentials") },
          { value: "deployments", label: t("storeDetail.deployments") },
          { value: "activity", label: t("storeDetail.activity") },
        ]}
      />

      {tab === "overview" ? (
        <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
          <Card className="xl:col-span-2">
            <CardHeader title={t("storeDetail.identity")} />
            <CardBody>
              <dl className="divide-y divide-outline-variant">
                <KeyValue label={t("common.identifier")}>
                  <Mono>{store.id}</Mono>
                </KeyValue>
                <KeyValue label={t("stores.domain")}>
                  <Mono>{store.primaryDomain}</Mono>
                </KeyValue>
                <KeyValue label={t("stores.version")}>
                  <Mono>{store.applicationVersion ?? "—"}</Mono>
                </KeyValue>
                <KeyValue label={t("stores.integration")}>
                  {t(`integrationStatus.${store.integrationStatus}`)}
                </KeyValue>
                <KeyValue label={t("stores.lastSeen")}>
                  {store.lastSeenAt ? formatRelative(store.lastSeenAt) : "—"}
                </KeyValue>
                <KeyValue label={t("stores.features")}>{store.installedFeatureCount ?? "—"}</KeyValue>
              </dl>
            </CardBody>
          </Card>

          <Card>
            <CardHeader title={t("storeDetail.usage")} />
            <CardBody className="flex flex-col gap-5">
              {usageData ? (
                <>
                  <AreaChart series={usageData.errors} label={t("storeDetail.errors")} tone="danger" />
                  <AreaChart series={usageData.logs} label={t("storeDetail.logVolume")} />
                  <AreaChart series={usageData.healthLatencyMs} label={t("storeDetail.probeLatency")} />
                  <p className="text-body-sm text-on-surface-variant">
                    {t("storeDetail.usageWindow", {
                      hours: usageData.windowHours,
                      errors: usageData.totalErrors,
                      logs: usageData.totalLogs,
                    })}
                  </p>
                </>
              ) : (
                <p className="text-body-sm text-on-surface-variant">{t("storeDetail.usageUnavailable")}</p>
              )}
            </CardBody>
          </Card>
        </div>
      ) : null}

      {tab === "features" ? (
        <CollectionCard query={installations}>
          {() => (
            <DataTable
              columns={installationColumns}
              rows={storeInstallations}
              rowKey={(row) => row.id}
              cardTitle={(row) => row.featureName}
              emptyMessage={t("common.noResults")}
            />
          )}
        </CollectionCard>
      ) : null}

      {tab === "domains" ? (
        <CollectionCard query={domains}>
          {(rows) => (
            <>
              <CardHeader
                title={t("storeDetail.domains")}
                icon={<Globe className="size-5" />}
                action={
                  can("store.manage") ? (
                    <span className="flex gap-2">
                      <Button
                        size="sm"
                        variant="outline"
                        disabled={startVerification.isPending}
                        onClick={() => startVerification.mutate()}
                      >
                        {t("domains.startVerification")}
                      </Button>
                      <Button
                        size="sm"
                        variant="outline"
                        disabled={verifyDomain.isPending}
                        onClick={() => verifyDomain.mutate()}
                      >
                        {t("domains.verify")}
                      </Button>
                    </span>
                  ) : undefined
                }
              />
              <DataTable
                columns={domainColumns}
                rows={rows}
                rowKey={(row) => row.id}
                cardTitle={(row) => (
                  <span dir="ltr" className="font-mono">
                    {row.host}
                  </span>
                )}
                emptyMessage={t("domains.none")}
              />
            </>
          )}
        </CollectionCard>
      ) : null}

      {tab === "credentials" ? (
        <CollectionCard query={credentials}>
          {(rows) => (
            <>
              <CardHeader
                title={t("storeDetail.credentials")}
                icon={<KeyRound className="size-5" />}
                action={
                  can("store.credentials.manage") ? (
                    <Button
                      size="sm"
                      disabled={issueCredential.isPending}
                      onClick={() =>
                        issueCredential.mutate(undefined, {
                          onSuccess: (credential) => setIssued(credential),
                        })
                      }
                    >
                      <RefreshCw className="size-4 rtl:-scale-x-100" aria-hidden />
                      {t("stores.issueCredentials")}
                    </Button>
                  ) : undefined
                }
              />
              {issued ? (
                <div className="m-4 rounded-md border border-warning/40 bg-warning/10 p-3">
                  <p className="text-body-sm text-on-surface">{t("stores.secretShownOnce")}</p>
                  <p dir="ltr" className="mt-2 break-all font-mono text-label text-on-surface">
                    {issued.clientId}
                  </p>
                  <p dir="ltr" className="break-all font-mono text-label text-on-surface">
                    {issued.clientSecret}
                  </p>
                </div>
              ) : null}
              <DataTable
                columns={credentialColumns}
                rows={rows}
                rowKey={(row) => row.id}
                cardTitle={(row) => (
                  <span dir="ltr" className="font-mono">
                    {row.clientId}
                  </span>
                )}
                emptyMessage={t("credentials.none")}
              />
              <CardBody className="border-t border-outline-variant text-body-sm text-on-surface-variant">
                {t("credentials.note")}
              </CardBody>
            </>
          )}
        </CollectionCard>
      ) : null}

      {tab === "deployments" ? (
        <CollectionCard query={deployments}>
          {(rows) => (
            <DataTable
              columns={deploymentColumns}
              rows={rows}
              rowKey={(row) => row.id}
              cardTitle={(row) => (
                <span dir="ltr" className="font-mono">
                  {row.version}
                </span>
              )}
              emptyMessage={t("common.noResults")}
            />
          )}
        </CollectionCard>
      ) : null}

      {tab === "activity" ? (
        <CollectionCard query={activity}>
          {(rows) => (
            <CardBody>
              <Timeline
                items={rows.map((entry) => ({
                  id: entry.id,
                  title: entry.title,
                  meta: `${entry.actor} · ${formatRelative(entry.occurredAt)}`,
                  tone:
                    entry.kind === "warning"
                      ? ("warning" as const)
                      : entry.kind === "backup"
                        ? ("success" as const)
                        : ("default" as const),
                }))}
              />
            </CardBody>
          )}
        </CollectionCard>
      ) : null}
    </PageShell>
  );
}
