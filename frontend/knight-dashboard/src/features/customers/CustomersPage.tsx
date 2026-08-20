import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router-dom";
import { Plus, Search } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import type { Customer, CustomerStatus } from "@/lib/api/domain";
import { PageShell, PageHeader, Toolbar, FilterTabs, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatNumber } from "@/lib/utils/format";
import { planLabel } from "@/lib/utils/planLabel";

const statusTone: Record<CustomerStatus, Tone> = {
  Active: "success",
  Prospect: "info",
  Suspended: "warning",
  Archived: "neutral",
};

type Filter = "all" | CustomerStatus;

export function CustomersPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const query = useCollection<Customer>("/customers");
  const can = useAuthStore((state) => state.can);
  const [filter, setFilter] = useState<Filter>("all");
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<Customer | null>(null);

  const all = query.data ?? [];
  const rows = all.filter(
    (customer) =>
      (filter === "all" || customer.status === filter) &&
      (search === "" ||
        customer.name.includes(search) ||
        customer.contactEmail.toLowerCase().includes(search.toLowerCase())),
  );

  const columns: Column<Customer>[] = [
    {
      key: "name",
      header: t("customers.name"),
      render: (row) => (
        <span className="flex flex-col">
          <span className="font-medium text-on-surface">{row.name}</span>
          <Mono>{row.contactEmail}</Mono>
        </span>
      ),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={statusTone[row.status]}>{t(`customerStatus.${row.status}`)}</StatusChip>
      ),
    },
    { key: "plan", header: t("customers.plan"), render: (row) => planLabel(t, row.planKey) },
    {
      key: "stores",
      header: t("customers.stores"),
      numeric: true,
      render: (row) => formatNumber(row.storeCount),
    },
    {
      key: "createdAt",
      header: t("customers.createdAt"),
      secondary: true,
      render: (row) => <Mono>{formatDateTime(row.createdAt)}</Mono>,
    },
  ];

  return (
    <PageShell>
      <PageHeader
        title={t("nav.customers")}
        subtitle={t("customers.subtitle")}
        actions={
          can("customer.create") ? (
            <Button size="sm" onClick={() => navigate("/customers/new")}>
              <Plus className="size-4" aria-hidden />
              {t("customers.create")}
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
                  value: "Active",
                  label: t("customerStatus.Active"),
                  count: all.filter((c) => c.status === "Active").length,
                },
                {
                  value: "Suspended",
                  label: t("customerStatus.Suspended"),
                  count: all.filter((c) => c.status === "Suspended").length,
                },
                {
                  value: "Prospect",
                  label: t("customerStatus.Prospect"),
                  count: all.filter((c) => c.status === "Prospect").length,
                },
              ]}
            />
            <div className="relative ms-auto w-full sm:w-64">
              <Search
                className="pointer-events-none absolute inset-y-0 start-3 my-auto size-4 text-on-surface-variant"
                aria-hidden
              />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder={t("common.search")}
                aria-label={t("common.search")}
                className="h-9 w-full rounded-md border border-outline-variant bg-surface-low ps-9 pe-3 text-body-sm text-on-surface focus:border-primary focus:outline-none"
              />
            </div>
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
        subtitle={selected?.contactEmail}
        onClose={() => setSelected(null)}
        footer={
          can("customer.update") ? (
            <>
              <Button variant="outline" size="sm">
                {t("customers.suspend")}
              </Button>
              <Button size="sm" onClick={() => navigate(`/customers/${selected?.id ?? ""}`)}>
                {t("customerDetail.open")}
              </Button>
            </>
          ) : undefined
        }
      >
        {selected ? (
          <dl className="divide-y divide-outline-variant">
            <KeyValue label={t("common.status")}>
              <StatusChip tone={statusTone[selected.status]}>
                {t(`customerStatus.${selected.status}`)}
              </StatusChip>
            </KeyValue>
            <KeyValue label={t("customers.plan")}>{planLabel(t, selected.planKey)}</KeyValue>
            <KeyValue label={t("customers.stores")}>{formatNumber(selected.storeCount)}</KeyValue>
            <KeyValue label={t("customers.createdAt")}>
              <Mono>{formatDateTime(selected.createdAt)}</Mono>
            </KeyValue>
            <KeyValue label={t("common.identifier")}>
              <Mono>{selected.id}</Mono>
            </KeyValue>
          </dl>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
