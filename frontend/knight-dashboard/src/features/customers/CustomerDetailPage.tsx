import { useState } from "react";
import { useParams, Link, useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { ChevronLeft, Ban, ExternalLink, Plus } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import type { AdminUser, Customer, Installation, Invoice, Store, Subscription } from "@/lib/api/domain";
import type { ActivityEntry, CustomerNote } from "@/lib/api/fixtures-detail";
import { PageShell, PageHeader, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Tabs, Timeline } from "@/components/data/Tabs";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { LoadingBlock, ErrorBlock } from "@/components/ui/StateBlock";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatNumber, formatRelative } from "@/lib/utils/format";
import { installationTone } from "@/features/installations/installationTone";

type Tab = "overview" | "stores" | "entitlements" | "admins" | "billing" | "activity";

const customerTone: Record<Customer["status"], Tone> = {
  Active: "success",
  Prospect: "info",
  Suspended: "warning",
  Archived: "neutral",
};

export function CustomerDetailPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { customerId = "" } = useParams();
  const can = useAuthStore((state) => state.can);
  const [tab, setTab] = useState<Tab>("overview");

  const customers = useCollection<Customer>("/customers");
  const stores = useCollection<Store>("/stores");
  const installations = useCollection<Installation>("/installations");
  const subscriptions = useCollection<Subscription>("/subscriptions");
  const invoices = useCollection<Invoice>("/invoices");
  const users = useCollection<AdminUser>("/users");
  const activity = useCollection<ActivityEntry>(`/customers/${customerId}/activity`);
  const notes = useCollection<CustomerNote>(`/customers/${customerId}/notes`);

  const customer = (customers.data ?? []).find((item) => item.id === customerId);
  const customerStores = (stores.data ?? []).filter((item) => item.customerId === customerId);
  const storeIds = new Set(customerStores.map((item) => item.id));
  const customerInstallations = (installations.data ?? []).filter((item) => storeIds.has(item.storeId));
  const subscription = (subscriptions.data ?? []).find((item) => item.customerId === customerId);
  const customerInvoices = (invoices.data ?? []).filter((item) => item.customerName === customer?.name);
  const customerAdmins = (users.data ?? []).filter((item) => item.customerName === customer?.name);

  if (customers.isPending) {
    return (
      <PageShell>
        <Card>
          <LoadingBlock rows={6} />
        </Card>
      </PageShell>
    );
  }

  if (customers.isError || !customer) {
    return (
      <PageShell>
        <Card>
          <ErrorBlock message={customers.error?.message ?? t("common.noResults")} />
        </Card>
      </PageShell>
    );
  }

  const storeColumns: Column<Store>[] = [
    { key: "domain", header: t("stores.domain"), mono: true, render: (row) => row.primaryDomain },
    { key: "environment", header: t("stores.environment"), render: (row) => t(`environment.${row.environment}`) },
    {
      key: "integration",
      header: t("stores.integration"),
      render: (row) => (
        <StatusChip tone={row.integrationStatus === "Connected" ? "success" : "warning"}>
          {t(`integrationStatus.${row.integrationStatus}`)}
        </StatusChip>
      ),
    },
    { key: "version", header: t("stores.version"), mono: true, render: (row) => row.applicationVersion ?? "—" },
    { key: "features", header: t("stores.features"), numeric: true, render: (row) => row.installedFeatureCount },
  ];

  const entitlementColumns: Column<Installation>[] = [
    { key: "feature", header: t("installations.feature"), render: (row) => row.featureName },
    { key: "store", header: t("installations.store"), mono: true, render: (row) => row.storeName },
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

  const adminColumns: Column<AdminUser>[] = [
    {
      key: "user",
      header: t("access.user"),
      render: (row) => (
        <span className="flex flex-col">
          <span className="text-on-surface">{row.displayName}</span>
          <Mono>{row.email}</Mono>
        </span>
      ),
    },
    { key: "roles", header: t("access.rolesColumn"), render: (row) => row.roles.join("، ") },
    {
      key: "mfa",
      header: t("access.mfa"),
      render: (row) => (
        <StatusChip tone={row.mfaEnabled ? "success" : "warning"}>
          {row.mfaEnabled ? t("common.yes") : t("common.no")}
        </StatusChip>
      ),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={row.status === "Active" ? "success" : "warning"}>
          {t(`userStatus.${row.status}`)}
        </StatusChip>
      ),
    },
    { key: "lastSeen", header: t("access.lastSeen"), render: (row) => (row.lastSeenAt ? formatRelative(row.lastSeenAt) : "—") },
  ];

  const invoiceColumns: Column<Invoice>[] = [
    { key: "number", header: t("billing.number"), mono: true, render: (row) => row.number },
    {
      key: "total",
      header: t("billing.total"),
      numeric: true,
      render: (row) => (row.total === 0 ? t("plans.free") : `${formatNumber(row.total)} ${t("billing.currency")}`),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={row.status === "Paid" ? "success" : row.status === "Overdue" ? "danger" : "info"}>
          {t(`invoiceStatus.${row.status}`)}
        </StatusChip>
      ),
    },
    { key: "issued", header: t("billing.issuedAt"), render: (row) => (row.issuedAt ? formatDateTime(row.issuedAt) : "—") },
  ];

  return (
    <PageShell>
      <Link
        to="/customers"
        className="flex w-fit items-center gap-1 text-body-sm text-on-surface-variant hover:text-on-surface"
      >
        <ChevronLeft className="size-4 rtl:-scale-x-100" aria-hidden />
        {t("nav.customers")}
      </Link>

      <PageHeader
        title={customer.name}
        subtitle={`${customer.contactEmail} · ${t(`planKey.${customer.planKey}`)}`}
        actions={
          can("customer.update") ? (
            <>
              <Button variant="outline" size="sm">
                <Ban className="size-4" aria-hidden />
                {t("customers.suspend")}
              </Button>
              <Button size="sm">{t("common.edit")}</Button>
            </>
          ) : undefined
        }
      />

      <Tabs<Tab>
        value={tab}
        onChange={setTab}
        options={[
          { value: "overview", label: t("customerDetail.overview") },
          { value: "stores", label: t("customerDetail.stores"), count: customerStores.length },
          { value: "entitlements", label: t("customerDetail.entitlements"), count: customerInstallations.length },
          { value: "admins", label: t("customerDetail.admins"), count: customerAdmins.length },
          { value: "billing", label: t("customerDetail.billing"), count: customerInvoices.length },
          { value: "activity", label: t("customerDetail.activity") },
        ]}
      />

      {tab === "overview" ? (
        <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
          <Card className="xl:col-span-2">
            <CardHeader title={t("customerDetail.identity")} />
            <CardBody>
              <dl className="divide-y divide-outline-variant">
                <KeyValue label={t("common.identifier")}>
                  <Mono>{customer.id}</Mono>
                </KeyValue>
                <KeyValue label={t("common.status")}>
                  <StatusChip tone={customerTone[customer.status]}>
                    {t(`customerStatus.${customer.status}`)}
                  </StatusChip>
                </KeyValue>
                <KeyValue label={t("customers.plan")}>{t(`planKey.${customer.planKey}`)}</KeyValue>
                <KeyValue label={t("customers.stores")}>{formatNumber(customer.storeCount)}</KeyValue>
                <KeyValue label={t("customers.createdAt")}>
                  <Mono>{formatDateTime(customer.createdAt)}</Mono>
                </KeyValue>
              </dl>
            </CardBody>
          </Card>

          <Card>
            <CardHeader title={t("customerDetail.subscription")} />
            <CardBody>
              {subscription ? (
                <dl className="divide-y divide-outline-variant">
                  <KeyValue label={t("subscriptions.plan")}>{subscription.planName}</KeyValue>
                  <KeyValue label={t("common.status")}>
                    <StatusChip tone={subscription.status === "Active" ? "success" : "warning"}>
                      {t(`subscriptionStatus.${subscription.status}`)}
                    </StatusChip>
                  </KeyValue>
                  <KeyValue label={t("subscriptions.optionalFeatures")}>
                    {formatNumber(subscription.optionalFeatures)}
                  </KeyValue>
                  <KeyValue label={t("subscriptions.monthlyTotal")}>
                    {subscription.monthlyTotal === 0
                      ? t("plans.free")
                      : `${formatNumber(subscription.monthlyTotal)} ${t("billing.currency")}`}
                  </KeyValue>
                  <KeyValue label={t("subscriptions.periodEnd")}>
                    {formatRelative(subscription.currentPeriodEnd)}
                  </KeyValue>
                </dl>
              ) : (
                <p className="text-body-sm text-on-surface-variant">{t("customerDetail.noSubscription")}</p>
              )}
            </CardBody>
          </Card>

          <Card className="xl:col-span-3">
            <CardHeader
              title={t("customerDetail.notes")}
              action={
                can("customer.update") ? (
                  <Button variant="outline" size="sm">
                    <Plus className="size-4" aria-hidden />
                    {t("customerDetail.addNote")}
                  </Button>
                ) : undefined
              }
            />
            <CardBody>
              {(notes.data ?? []).length === 0 ? (
                <p className="text-body-sm text-on-surface-variant">{t("customerDetail.noNotes")}</p>
              ) : (
                <ul className="flex flex-col gap-3">
                  {(notes.data ?? []).map((note) => (
                    <li key={note.id} className="rounded-md bg-surface-low p-4">
                      <p className="text-body-sm text-on-surface">{note.body}</p>
                      <p className="mt-2 text-body-sm text-on-surface-variant">
                        {note.author} · {formatRelative(note.createdAt)}
                      </p>
                    </li>
                  ))}
                </ul>
              )}
            </CardBody>
          </Card>
        </div>
      ) : null}

      {tab === "stores" ? (
        <CollectionCard query={stores}>
          {() => (
            <DataTable
              columns={storeColumns}
              rows={customerStores}
              rowKey={(row) => row.id}
              onRowClick={(row) => navigate(`/stores/${row.id}`)}
              cardTitle={(row) => (
                <span dir="ltr" className="font-mono">
                  {row.primaryDomain}
                </span>
              )}
              emptyMessage={t("customerDetail.noStores")}
            />
          )}
        </CollectionCard>
      ) : null}

      {tab === "entitlements" ? (
        <CollectionCard query={installations}>
          {() => (
            <>
              <CardHeader title={t("customerDetail.entitlements")} />
              <DataTable
                columns={entitlementColumns}
                rows={customerInstallations}
                rowKey={(row) => row.id}
                cardTitle={(row) => row.featureName}
                emptyMessage={t("common.noResults")}
              />
              <CardBody className="border-t border-outline-variant text-body-sm text-on-surface-variant">
                {t("customerDetail.entitlementNote")}
              </CardBody>
            </>
          )}
        </CollectionCard>
      ) : null}

      {tab === "admins" ? (
        <CollectionCard query={users}>
          {() => (
            <DataTable
              columns={adminColumns}
              rows={customerAdmins}
              rowKey={(row) => row.id}
              cardTitle={(row) => row.displayName}
              emptyMessage={t("customerDetail.noAdmins")}
            />
          )}
        </CollectionCard>
      ) : null}

      {tab === "billing" ? (
        <CollectionCard query={invoices}>
          {() => (
            <DataTable
              columns={invoiceColumns}
              rows={customerInvoices}
              rowKey={(row) => row.id}
              cardTitle={(row) => (
                <span dir="ltr" className="font-mono">
                  {row.number}
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
                  tone: entry.kind === "warning" ? ("warning" as const) : ("default" as const),
                }))}
              />
            </CardBody>
          )}
        </CollectionCard>
      ) : null}

      <p className="flex items-center gap-1.5 text-body-sm text-on-surface-variant">
        <ExternalLink className="size-4 rtl:-scale-x-100" aria-hidden />
        {t("customerDetail.isolationNote")}
      </p>
    </PageShell>
  );
}
