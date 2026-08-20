import { lazy, Suspense, type ComponentType } from "react";
import { Route } from "react-router-dom";
import { LoadingBlock } from "@/components/ui/StateBlock";
import { RouteErrorBoundary } from "./RouteErrorBoundary";

/** Every feature area is a lazily loaded route chunk. */
const pages: [path: string, loader: () => Promise<{ default: ComponentType }>][] = [
  ["/", () => import("@/features/dashboard/pages/DashboardPage").then((m) => ({ default: m.DashboardPage }))],
  ["/customers", () => import("@/features/customers/CustomersPage").then((m) => ({ default: m.CustomersPage }))],
  ["/customers/new", () => import("@/features/customers/CreateCustomerPage").then((m) => ({ default: m.CreateCustomerPage }))],
  ["/customers/:customerId", () => import("@/features/customers/CustomerDetailPage").then((m) => ({ default: m.CustomerDetailPage }))],
  ["/stores", () => import("@/features/stores/StoresPage").then((m) => ({ default: m.StoresPage }))],
  ["/stores/:storeId", () => import("@/features/stores/StoreDetailPage").then((m) => ({ default: m.StoreDetailPage }))],
  ["/features", () => import("@/features/features/FeaturesPage").then((m) => ({ default: m.FeaturesPage }))],
  ["/store-images", () => import("@/features/features/StoreImagesPage").then((m) => ({ default: m.StoreImagesPage }))],
  ["/rollouts", () => import("@/features/features/RolloutsPage").then((m) => ({ default: m.RolloutsPage }))],
  ["/installations", () => import("@/features/installations/InstallationsPage").then((m) => ({ default: m.InstallationsPage }))],
  ["/plans", () => import("@/features/plans/PlansPage").then((m) => ({ default: m.PlansPage }))],
  ["/billing", () => import("@/features/billing/BillingPage").then((m) => ({ default: m.BillingPage }))],
  ["/infrastructure", () => import("@/features/infrastructure/InfrastructurePage").then((m) => ({ default: m.InfrastructurePage }))],
  ["/monitoring", () => import("@/features/monitoring/MonitoringPage").then((m) => ({ default: m.MonitoringPage }))],
  ["/alerts", () => import("@/features/alerts/AlertsPage").then((m) => ({ default: m.AlertsPage }))],
  ["/errors", () => import("@/features/errors/ErrorsPage").then((m) => ({ default: m.ErrorsPage }))],
  ["/incidents", () => import("@/features/incidents/IncidentsPage").then((m) => ({ default: m.IncidentsPage }))],
  ["/logs", () => import("@/features/logs/LogsPage").then((m) => ({ default: m.LogsPage }))],
  ["/reports", () => import("@/features/reports/ReportsPage").then((m) => ({ default: m.ReportsPage }))],
  ["/access", () => import("@/features/access/AccessPage").then((m) => ({ default: m.AccessPage }))],
  ["/audit", () => import("@/features/audit/AuditPage").then((m) => ({ default: m.AuditPage }))],
  ["/settings", () => import("@/features/settings/SettingsPage").then((m) => ({ default: m.SettingsPage }))],
];

export function featureRoutes() {
  return pages.map(([path, loader]) => {
    const Page = lazy(loader);
    return (
      <Route
        key={path}
        path={path}
        index={path === "/"}
        element={
          // Each screen is wrapped on its own so one failing page cannot blank
          // the whole application.
          <RouteErrorBoundary resetKey={path}>
            <Suspense fallback={<LoadingBlock rows={6} />}>
              <Page />
            </Suspense>
          </RouteErrorBoundary>
        }
      />
    );
  });
}
