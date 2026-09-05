import {
  LayoutDashboard,
  Building2,
  Store,
  ServerCog,
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
}

// The permission each destination needs is not repeated here: it lives once in
// app/permissions.ts, which the router reads too, so an item cannot be hidden
// from this menu yet left reachable by its URL (or the reverse).

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
      { key: "customers", to: "/customers", icon: Building2 },
      { key: "stores", to: "/stores", icon: Store },
      { key: "provisioning", to: "/provisioning", icon: ServerCog },
    ],
  },
  {
    key: "sectionService",
    items: [
      { key: "features", to: "/features", icon: Package },
      { key: "storeImages", to: "/store-images", icon: Boxes },
      { key: "rollouts", to: "/rollouts", icon: Rocket },
      {
        key: "installations",
        to: "/installations",
        icon: PlayCircle,
      },
      { key: "plans", to: "/plans", icon: CreditCard },
      { key: "billing", to: "/billing", icon: Receipt },
    ],
  },
  {
    key: "sectionInfra",
    items: [
      { key: "infrastructure", to: "/infrastructure", icon: Server },
      { key: "monitoring", to: "/monitoring", icon: Activity },
      { key: "alerts", to: "/alerts", icon: BellRing },
      { key: "errors", to: "/errors", icon: Bug },
      { key: "incidents", to: "/incidents", icon: Siren },
      { key: "logs", to: "/logs", icon: ScrollText },
    ],
  },
  {
    key: "sectionGovernance",
    items: [
      { key: "reports", to: "/reports", icon: BarChart3 },
      { key: "access", to: "/access", icon: Users },
      { key: "audit", to: "/audit", icon: ShieldCheck },
      { key: "settings", to: "/settings", icon: Settings },
    ],
  },
];
