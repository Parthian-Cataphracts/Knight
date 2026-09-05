import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { mockApi, renderScreen } from "@/test-utils";
import { setAccessToken } from "@/lib/api/client";
import { PortalAutoAdminPage } from "@/features/portal/pages/PortalAutoAdminPage";

/**
 * The Automatic Admin portal page, rendered against payloads shaped the way the
 * API actually shapes them (docs/adr/0038) — the same discipline as
 * features/__tests__/screens.test.tsx: the assertion that matters is that the
 * screen renders its real data without throwing.
 */

const customPlan = {
  id: "11111111-1111-1111-1111-111111111111",
  key: "custom",
  name: "Custom",
  description: "À la carte.",
  basePrice: 99,
  currency: "EUR",
  includedFeatures: [],
  optionalFeatures: [
    { featureId: "F-IMG", slug: "auto-admin-image", name: "Image generation", description: "Studio images.", price: 12, currency: "EUR" },
    { featureId: "F-TG", slug: "auto-admin-telegram", name: "Telegram publishing", description: "Post to Telegram.", price: 6, currency: "EUR" },
    { featureId: "F-CAP", slug: "auto-admin-caption", name: "Caption writing", description: "Captions.", price: 9, currency: "EUR" },
  ],
};

const draftRun = {
  id: "22222222-2222-2222-2222-222222222222",
  topic: "Yalda sale on all rugs",
  autonomy: "ApprovalRequired",
  status: "Draft",
  hasPublicationErrors: false,
  drafts: [{ kind: "Image", body: "a studio-style shot for Yalda sale", generatorName: "simulated" }],
  publications: [],
  createdAt: new Date().toISOString(),
  updatedAt: null,
};

beforeEach(() => {
  setAccessToken("test-token");
  vi.unstubAllGlobals();
});

describe("PortalAutoAdminPage", () => {
  it("shows the engine and a draft run once the customer owns a part", async () => {
    mockApi({
      "/catalog/plans": [customPlan],
      // Owns the image generation part and the Telegram channel.
      "/me/subscription": { id: "s1", planId: customPlan.id, planName: "Custom", status: "active", currentPeriodEnd: new Date().toISOString(), cancelAtPeriodEnd: false, featureIds: ["F-IMG", "F-TG"] },
      "/me/auto-admin/settings": { autonomy: "ApprovalRequired" },
      "/me/auto-admin/runs": [draftRun],
    });

    renderScreen(<PortalAutoAdminPage />, { route: "/portal/auto-admin" });

    // The run's report renders from the real shape.
    expect(await screen.findByText("Yalda sale on all rugs")).toBeInTheDocument();
    expect(screen.getByText("a studio-style shot for Yalda sale")).toBeInTheDocument();
    // A draft offers approval.
    expect(screen.getByRole("button", { name: /Approve & publish/i })).toBeInTheDocument();
    // The run panel is present.
    expect(screen.getByRole("button", { name: "Generate" })).toBeInTheDocument();
    // An owned part reads as active.
    expect(screen.getAllByText("Active").length).toBeGreaterThan(0);
  });

  it("shows the storefront when the customer owns no parts", async () => {
    mockApi({
      "/catalog/plans": [customPlan],
      "/me/subscription": { id: "s1", planId: customPlan.id, planName: "Custom", status: "active", currentPeriodEnd: new Date().toISOString(), cancelAtPeriodEnd: false, featureIds: [] },
      "/me/auto-admin/settings": { autonomy: "ApprovalRequired" },
      "/me/auto-admin/runs": [],
    });

    renderScreen(<PortalAutoAdminPage />, { route: "/portal/auto-admin" });

    expect(await screen.findByText("Hire a virtual admin")).toBeInTheDocument();
    // No run panel before the customer owns a part.
    expect(screen.queryByRole("button", { name: "Generate" })).not.toBeInTheDocument();
  });
});
