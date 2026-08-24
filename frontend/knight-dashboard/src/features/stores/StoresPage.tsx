import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { Plus, KeyRound, RefreshCw } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import type { Customer, Installation, IntegrationStatus, Store } from "@/lib/api/domain";
import { PageShell, PageHeader, Toolbar, FilterTabs, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { EditDrawer } from "@/features/shared/EditDrawer";
import { useAuthStore } from "@/store/auth";
import { formatRelative } from "@/lib/utils/format";
import { installationTone } from "@/features/installations/installationTone";

const integrationTone: Record<IntegrationStatus, Tone> = {
  Connected: "success",
  Degraded: "warning",
  Pending: "info",
  Disconnected: "danger",
  NotRegistered: "neutral",
};

type Filter = "all" | "Production" | "Staging" | "Development";

export function StoresPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const query = useCollection<Store>("/stores");
  const installations = useCollection<Installation>("/installations");
  const can = useAuthStore((state) => state.can);
  const [filter, setFilter] = useState<Filter>("all");
  const [selected, setSelected] = useState<Store | null>(null);
  const [registering, setRegistering] = useState(false);

  // A store belongs to a customer, so registering one means choosing which. The
  // list is only fetched for that, and only by somebody who may create a store.
  const customers = useCollection<Customer>("/customers", can("store.create"));
  const customerChoices = (customers.data ?? []).map((customer) => ({
    value: customer.id,
    label: customer.name,
  }));

  const all = query.data ?? [];
  const rows = all.filter((store) => filter === "all" || store.environment === filter);
  const storeInstallations = (installations.data ?? []).filter(
    (item) => item.storeId === selected?.id,
  );

  const columns: Column<Store>[] = [
    {
      key: "domain",
      header: t("stores.domain"),
      render: (row) => (
        <span className="flex flex-col">
          <span dir="ltr" className="font-mono text-body-sm text-on-surface">
            {row.primaryDomain}
          </span>
          <span className="text-body-sm text-on-surface-variant">{row.customerName}</span>
        </span>
      ),
    },
    {
      key: "environment",
      header: t("stores.environment"),
      render: (row) => (
        <StatusChip tone={row.environment === "Production" ? "info" : "neutral"}>
          {t(`environment.${row.environment}`)}
        </StatusChip>
      ),
    },
    {
      key: "integration",
      header: t("stores.integration"),
      render: (row) => (
        <StatusChip tone={integrationTone[row.integrationStatus]}>
          {t(`integrationStatus.${row.integrationStatus}`)}
        </StatusChip>
      ),
    },
    {
      key: "version",
      header: t("stores.version"),
      mono: true,
      render: (row) => row.applicationVersion ?? "—",
    },
    {
      key: "hosting",
      header: t("stores.hosting"),
      secondary: true,
      render: (row) => t(`hosting.${row.hostingModel}`),
    },
    {
      key: "features",
      header: t("stores.features"),
      numeric: true,
      render: (row) => row.installedFeatureCount ?? "—",
    },
    {
      key: "lastSeen",
      header: t("stores.lastSeen"),
      secondary: true,
      render: (row) => (row.lastSeenAt ? formatRelative(row.lastSeenAt) : "—"),
    },
  ];

  return (
    <PageShell>
      <EditDrawer
        open={registering}
        title={t("stores.register")}
        subtitle={t("stores.registerSubtitle")}
        path="/stores"
        method="POST"
        fields={[
          {
            key: "customerId",
            label: t("stores.customer"),
            // The select shows the first option whatever the form holds, so an
            // empty value here would look chosen and post nothing.
            value: customerChoices[0]?.value ?? "",
            choices: customerChoices,
            ...(customerChoices.length === 0 ? { note: t("stores.noCustomers") } : {}),
          },
          { key: "name", label: t("common.name"), value: "" },
          {
            key: "slug",
            label: t("stores.slug"),
            value: "",
            ltr: true,
            placeholder: "cafe-parthia",
          },
          {
            key: "primaryDomain",
            label: t("stores.primaryDomain"),
            value: "",
            ltr: true,
            placeholder: "cafe1.ir",
            note: t("stores.domainNote"),
          },
          {
            key: "environment",
            label: t("stores.environment"),
            value: "Production",
            choices: [
              { value: "Production", label: t("environment.Production") },
              { value: "Staging", label: t("environment.Staging") },
              { value: "Development", label: t("environment.Development") },
            ],
          },
          {
            key: "hostingModel",
            label: t("stores.hosting"),
            value: "SharedManaged",
            choices: [
              { value: "SharedManaged", label: t("hosting.SharedManaged") },
              { value: "DedicatedManaged", label: t("hosting.DedicatedManaged") },
              { value: "CustomerManaged", label: t("hosting.CustomerManaged") },
            ],
          },
        ]}
        onClose={() => setRegistering(false)}
        onSaved={() => {
          setRegistering(false);
          void query.refetch();
        }}
      />

      <PageHeader
        title={t("nav.stores")}
        subtitle={t("stores.subtitle")}
        actions={
          can("store.create") ? (
            <Button size="sm" onClick={() => setRegistering(true)}>
              <Plus className="size-4" aria-hidden />
              {t("stores.register")}
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
                  value: "Production",
                  label: t("environment.Production"),
                  count: all.filter((s) => s.environment === "Production").length,
                },
                {
                  value: "Staging",
                  label: t("environment.Staging"),
                  count: all.filter((s) => s.environment === "Staging").length,
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
            cardTitle={(row) => (
              <span dir="ltr" className="font-mono">
                {row.primaryDomain}
              </span>
            )}
            emptyMessage={t("common.noResults")}
          />
        )}
      </CollectionCard>

      <Drawer
        open={selected !== null}
        title={selected?.primaryDomain ?? ""}
        subtitle={selected?.customerName}
        onClose={() => setSelected(null)}
        footer={
          can("store.credentials.manage") ? (
            <>
              <Button variant="outline" size="sm">
                <RefreshCw className="size-4 rtl:-scale-x-100" aria-hidden />
                {t("stores.rotate")}
              </Button>
              <Button size="sm" onClick={() => navigate(`/stores/${selected?.id ?? ""}`)}>
                <KeyRound className="size-4" aria-hidden />
                {t("storeDetail.open")}
              </Button>
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-6">
            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("stores.environment")}>
                {t(`environment.${selected.environment}`)}
              </KeyValue>
              <KeyValue label={t("stores.integration")}>
                <StatusChip tone={integrationTone[selected.integrationStatus]}>
                  {t(`integrationStatus.${selected.integrationStatus}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("stores.version")}>
                <Mono>{selected.applicationVersion ?? "—"}</Mono>
              </KeyValue>
              <KeyValue label={t("stores.hosting")}>
                {t(`hosting.${selected.hostingModel}`)}
              </KeyValue>
              <KeyValue label={t("stores.lastSeen")}>
                {selected.lastSeenAt ? formatRelative(selected.lastSeenAt) : "—"}
              </KeyValue>
              <KeyValue label={t("common.identifier")}>
                <Mono>{selected.id}</Mono>
              </KeyValue>
            </dl>

            <section>
              <h3 className="label-caps mb-3 text-on-surface-variant/80">
                {t("stores.installedFeatures")}
              </h3>
              {storeInstallations.length === 0 ? (
                <p className="text-body-sm text-on-surface-variant">{t("common.noResults")}</p>
              ) : (
                <ul className="flex flex-col gap-2">
                  {storeInstallations.map((item) => (
                    <li
                      key={item.id}
                      className="flex items-center justify-between gap-3 rounded-md bg-surface-low px-3 py-2.5"
                    >
                      <span className="flex min-w-0 flex-col">
                        <span className="truncate text-body-sm text-on-surface">
                          {item.featureName}
                        </span>
                        <Mono>{item.installedVersion ?? t("installations.notInstalledShort")}</Mono>
                      </span>
                      <StatusChip tone={installationTone[item.state]}>
                        {t(`installationState.${item.state}`)}
                      </StatusChip>
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
