import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { mockApi, paged, renderScreen } from "@/test-utils";
import { setAccessToken } from "@/lib/api/client";
import { PlanFeaturesDialog } from "@/features/plans/PlanFeaturesDialog";
import type { Feature, Plan } from "@/lib/api/domain";

/**
 * The operator's plan composer, rendered against the shapes the API returns
 * (phase 28): membership reflects the plan, and a feature's pricing — the
 * time-boxed rows the price endpoint returns — is reachable per feature.
 */

const plan: Plan = {
  id: "P1",
  key: "custom",
  name: "Custom",
  description: "À la carte.",
  basePrice: 99,
  currency: "EUR",
  isActive: true,
  sortOrder: 2,
  customerCount: 3,
  includedFeatures: ["catalog"],
  optionalFeatures: ["advanced-search"],
};

const feature = (id: string, slug: string, name: string): Feature => ({
  id,
  slug,
  name,
  description: "",
  category: "Test",
  status: "Published",
  isOptional: true,
  requiresDedicatedInfrastructure: false,
  latestVersion: null,
  installCount: null,
  entitledCount: 0,
  plans: [],
});

const price = {
  id: "PR1",
  featureId: "F-AS",
  planId: "P1",
  amount: 29,
  currency: "EUR",
  billingPeriod: "Monthly",
  validFrom: new Date().toISOString(),
  validTo: null,
};

beforeEach(() => {
  setAccessToken("test-token");
  vi.unstubAllGlobals();
});

describe("PlanFeaturesDialog", () => {
  it("renders each feature with its membership and reveals its pricing", async () => {
    mockApi({
      "/features": paged([
        feature("F-CAT", "catalog", "Catalogue"),
        feature("F-AS", "advanced-search", "Advanced Search"),
        feature("F-GC", "gift-cards", "Gift Cards"),
      ]),
      "/plans/prices": paged([price]),
    });

    renderScreen(<PlanFeaturesDialog plan={plan} onClose={() => {}} onChanged={() => {}} />);

    // Every published feature is listed, whether or not it is on the plan.
    expect(await screen.findByText("Catalogue")).toBeInTheDocument();
    expect(screen.getByText("Advanced Search")).toBeInTheDocument();
    expect(screen.getByText("Gift Cards")).toBeInTheDocument();

    // A feature the plan includes shows "Included" as the pressed segment.
    const included = screen.getAllByRole("button", { name: "Included", pressed: true });
    expect(included.length).toBeGreaterThan(0);

    // An in-plan feature exposes its pricing; a click reveals the price form.
    screen.getAllByRole("button", { name: /Pricing/i })[0]!.click();
    expect(await screen.findByRole("button", { name: /Set price/i })).toBeInTheDocument();
    // The time-boxed price row arrives once the price query resolves.
    expect((await screen.findAllByText(/EUR/)).length).toBeGreaterThan(0);
  });
});
