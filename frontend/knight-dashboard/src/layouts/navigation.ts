import {
  LayoutDashboard,
  Building2,
  Store,
  Package,
  Boxes,
  Rocket,
  PlayCircle,
  CreditCard,
  Receipt,
  Server,
  Activity,
  BellRing,
  Bug,
  Siren,
  ScrollText,
  BarChart3,
  Users,
  ShieldCheck,
  Settings,
  type LucideIcon,
} from "lucide-react";

export interface NavItem {
  key: string;
  to: string;
  icon: LucideIcon;
  /** Permission that makes this destination useful. UI convenience only. */
  permission?: string;
}

export interface NavSection {
  key: string;
  items: NavItem[];
}

/**
 * Information architecture adapted from the Stitch design (docs/design-system.md).
 * Two deliberate changes against that export:
 *   - "Tenant" is split into Customers and Stores, matching docs/domain-model.md.
 *   - Features and Installations are separate destinations, because entitlement
 *     and installation are separate facts (docs/feature-delivery.md).
 */
export const NAVIGATION: NavSection[] = [
  {
    key: "sectionOperations",
    items: [
      { key: "dashboard", to: "/", icon: LayoutDashboard },
      { key: "customers", to: "/customers", icon: Building2, permission: "customer.view" },
      { key: "stores", to: "/stores", icon: Store, permission: "store.view" },
    ],
  },
  {
    key: "sectionService",
    items: [
      { key: "features", to: "/features", icon: Package, permission: "feature.view" },
      { key: "storeImages", to: "/store-images", icon: Boxes, permission: "feature.view" },
      { key: "rollouts", to: "/rollouts", icon: Rocket, permission: "feature.publish" },
      {
        key: "installations",
        to: "/installations",
        icon: PlayCircle,
        permission: "installation.view",
      },
      { key: "plans", to: "/plans", icon: CreditCard, permission: "subscription.view" },
      { key: "billing", to: "/billing", icon: Receipt, permission: "billing.view" },
    ],
  },
  {
    key: "sectionInfra",
    items: [
      { key: "infrastructure", to: "/infrastructure", icon: Server, permission: "server.view" },
      { key: "monitoring", to: "/monitoring", icon: Activity, permission: "monitoring.view" },
      { key: "alerts", to: "/alerts", icon: BellRing, permission: "monitoring.view" },
      { key: "errors", to: "/errors", icon: Bug, permission: "errors.view" },
      { key: "incidents", to: "/incidents", icon: Siren, permission: "incident.view" },
      { key: "logs", to: "/logs", icon: ScrollText, permission: "logs.view" },
    ],
  },
  {
    key: "sectionGovernance",
    items: [
      { key: "reports", to: "/reports", icon: BarChart3, permission: "report.view" },
      { key: "access", to: "/access", icon: Users, permission: "user.view" },
      { key: "audit", to: "/audit", icon: ShieldCheck, permission: "audit.view" },
      { key: "settings", to: "/settings", icon: Settings },
    ],
  },
];
