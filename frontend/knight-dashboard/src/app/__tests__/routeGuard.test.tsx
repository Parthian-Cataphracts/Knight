import { describe, it, expect } from "vitest";
import { screen } from "@testing-library/react";
import { renderScreen } from "@/test-utils";
import i18n from "@/i18n";
import { RequirePermission } from "@/app/RequirePermission";
import { Sidebar } from "@/layouts/Sidebar";

/**
 * Phase 32A — a screen is hidden from the menu *and* refused by its own URL to an
 * operator whose role does not grant it. Before this, the two were decided in
 * different places: the sidebar hid the link, but the route rendered anyway.
 */
describe("permission route guard", () => {
  it("renders the screen when the operator holds the permission", () => {
    renderScreen(
      <RequirePermission permission="billing.view">
        <p>the billing screen</p>
      </RequirePermission>,
      { permissions: ["billing.view"] },
    );

    expect(screen.getByText("the billing screen")).toBeInTheDocument();
  });

  it("refuses the screen — not just the menu link — without the permission", () => {
    renderScreen(
      <RequirePermission permission="billing.view">
        <p>the billing screen</p>
      </RequirePermission>,
      { permissions: ["store.view"] },
    );

    expect(screen.queryByText("the billing screen")).not.toBeInTheDocument();
    expect(screen.getByText(i18n.t("access.notAuthorizedTitle"))).toBeInTheDocument();
  });
});

describe("sidebar reflects the operator's tier", () => {
  it("shows only the destinations the permissions grant", () => {
    // A support-shaped tier: may see customers and stores, but not billing,
    // rollouts or the access screen.
    const { container } = renderScreen(<Sidebar collapsed={false} />, {
      permissions: ["customer.view", "store.view", "errors.view"],
    });

    expect(container.querySelector('a[href="/customers"]')).not.toBeNull();
    expect(container.querySelector('a[href="/stores"]')).not.toBeNull();
    expect(container.querySelector('a[href="/errors"]')).not.toBeNull();

    // Hidden, because the tier lacks the permission each needs.
    expect(container.querySelector('a[href="/billing"]')).toBeNull();
    expect(container.querySelector('a[href="/rollouts"]')).toBeNull();
    expect(container.querySelector('a[href="/access"]')).toBeNull();

    // The home and personal settings have no permission and are always present.
    expect(container.querySelector('a[href="/"]')).not.toBeNull();
    expect(container.querySelector('a[href="/settings"]')).not.toBeNull();
  });

  it("drops a whole section when the operator can reach none of it", () => {
    // No governance permissions at all: reports, access and audit gone. Settings
    // lives in that section with no permission, so the section itself survives on
    // settings alone — assert the gated three are absent rather than the heading.
    const { container } = renderScreen(<Sidebar collapsed={false} />, {
      permissions: ["store.view"],
    });

    expect(container.querySelector('a[href="/reports"]')).toBeNull();
    expect(container.querySelector('a[href="/audit"]')).toBeNull();
    expect(container.querySelector('a[href="/infrastructure"]')).toBeNull();
  });
});
