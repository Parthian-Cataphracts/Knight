import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { Plus, KeyRound, RefreshCw } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
<<<<<<< HEAD
import type { Installation, IntegrationStatus, Store } from "@/lib/api/domain";
=======
import { apiRequest } from "@/lib/api/client";
import type {
  Customer,
  HostingModel,
  Installation,
  IntegrationStatus,
  Server,
  Store,
} from "@/lib/api/domain";
import type { Environment } from "@/lib/api/types";
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
import { PageShell, PageHeader, Toolbar, FilterTabs, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
<<<<<<< HEAD
=======
import { TextField } from "@/components/ui/TextField";
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
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
<<<<<<< HEAD
=======
  const [registering, setRegistering] = useState(false);
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5

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
<<<<<<< HEAD
=======
      <RegisterStoreForm
        open={registering}
        onClose={() => setRegistering(false)}
        onRegistered={() => {
          setRegistering(false);
          void query.refetch();
        }}
      />

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
      <PageHeader
        title={t("nav.stores")}
        subtitle={t("stores.subtitle")}
        actions={
          can("store.create") ? (
<<<<<<< HEAD
            <Button size="sm">
=======
            <Button size="sm" onClick={() => setRegistering(true)}>
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
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
<<<<<<< HEAD
=======

/**
 * Registering a store, including the machine it will run on.
 *
 * Its own form rather than the shared EditDrawer because the server list depends
 * on two answers given inside it: a dedicated machine belongs to one customer,
 * and a machine only hosts stores of its own environment. A static field list
 * cannot narrow itself as those change, and offering a machine that will be
 * refused is a worse experience than not offering it.
 *
 * The narrowing is a convenience, not the check. KNIGHT validates the placement
 * on its own and refuses a machine that is decommissioned, dedicated elsewhere
 * or in another environment - what appears here is only what would be accepted.
 */
function RegisterStoreForm({
  open,
  onClose,
  onRegistered,
}: {
  open: boolean;
  onClose: () => void;
  onRegistered: () => void;
}) {
  const { t } = useTranslation();
  const customers = useCollection<Customer>("/customers", open);
  const servers = useCollection<Server>("/servers", open);

  const [customerId, setCustomerId] = useState("");
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [primaryDomain, setPrimaryDomain] = useState("");
  const [environment, setEnvironment] = useState<Environment>("Production");
  const [hostingModel, setHostingModel] = useState<HostingModel>("SharedManaged");
  const [serverId, setServerId] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const customerList = customers.data ?? [];

  // Default to the first customer once they arrive, so the select never shows a
  // name while holding nothing.
  const chosenCustomer = customerId === "" ? (customerList[0]?.id ?? "") : customerId;

  const eligible = (servers.data ?? []).filter(
    (server) =>
      server.decommissionedAt === null &&
      server.environment === environment &&
      (server.dedicatedCustomerId === null || server.dedicatedCustomerId === chosenCustomer),
  );

  // The chosen machine may stop being eligible when the customer or environment
  // changes underneath it, and posting it anyway would be refused.
  const chosenServer = eligible.some((server) => server.id === serverId) ? serverId : "";

  const submit = async () => {
    setSaving(true);
    setError(null);

    try {
      await apiRequest("/stores", {
        method: "POST",
        body: {
          customerId: chosenCustomer,
          name: name.trim(),
          slug: slug.trim(),
          primaryDomain: primaryDomain.trim(),
          environment,
          hostingModel,
          serverId: chosenServer === "" ? null : chosenServer,
        },
      });

      setName("");
      setSlug("");
      setPrimaryDomain("");
      setServerId("");
      onRegistered();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSaving(false);
    }
  };

  const incomplete =
    chosenCustomer === "" || name.trim() === "" || slug.trim() === "" || primaryDomain.trim() === "";

  return (
    <Drawer
      open={open}
      title={t("stores.register")}
      subtitle={t("stores.registerSubtitle")}
      onClose={onClose}
      footer={
        <Button size="sm" disabled={saving || incomplete} onClick={() => void submit()}>
          {t("common.save")}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        {error ? (
          <p role="alert" className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container">
            {error}
          </p>
        ) : null}

        <SelectField
          label={t("stores.customer")}
          value={chosenCustomer}
          onChange={setCustomerId}
          options={customerList.map((customer) => ({ value: customer.id, label: customer.name }))}
          note={customerList.length === 0 ? t("stores.noCustomers") : undefined}
        />

        <TextField label={t("common.name")} value={name} onChange={(e) => setName(e.target.value)} />

        <TextField
          label={t("stores.slug")}
          dir="ltr"
          placeholder="phoenix-verify"
          hint={t("createCustomer.slugHint")}
          value={slug}
          onChange={(e) => setSlug(e.target.value)}
        />

        <TextField
          label={t("stores.primaryDomain")}
          dir="ltr"
          placeholder="cafe1.ir"
          hint={t("stores.domainNote")}
          value={primaryDomain}
          onChange={(e) => setPrimaryDomain(e.target.value)}
        />

        <SelectField
          label={t("stores.environment")}
          value={environment}
          onChange={(value) => setEnvironment(value as Environment)}
          options={(["Production", "Staging", "Development"] as const).map((value) => ({
            value,
            label: t(`environment.${value}`),
          }))}
        />

        <SelectField
          label={t("stores.hosting")}
          value={hostingModel}
          onChange={(value) => setHostingModel(value as HostingModel)}
          options={(["SharedManaged", "DedicatedManaged", "CustomerManaged"] as const).map((value) => ({
            value,
            label: t(`hosting.${value}`),
          }))}
        />

        <SelectField
          label={t("stores.server")}
          value={chosenServer}
          onChange={setServerId}
          options={[
            { value: "", label: t("stores.noServer") },
            ...eligible.map((server) => ({
              value: server.id,
              label:
                server.dedicatedCustomerId === null
                  ? `${server.name} · ${t("infrastructure.shared")}`
                  : `${server.name} · ${t("infrastructure.dedicate")}`,
            })),
          ]}
          note={eligible.length === 0 ? t("stores.noEligibleServers") : t("stores.serverNote")}
        />
      </div>
    </Drawer>
  );
}

/** A labelled select. The same shape EditDrawer draws, for the forms that cannot use it. */
function SelectField({
  label,
  value,
  onChange,
  options,
  note,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  options: { value: string; label: string }[];
  note?: string | undefined;
}) {
  const id = `field-${label}`;

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-body-sm font-medium text-on-surface-variant">
        {label}
      </label>
      <select
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="h-11 w-full rounded-md border border-outline-variant bg-surface-low px-3 text-body text-on-surface focus:border-primary focus:outline-none"
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
      {note ? <p className="text-body-sm text-on-surface-variant">{note}</p> : null}
    </div>
  );
}
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
