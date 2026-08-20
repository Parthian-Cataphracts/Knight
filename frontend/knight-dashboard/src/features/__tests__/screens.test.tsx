import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import { mockApi, paged, renderScreen } from "@/test-utils";
import { setAccessToken } from "@/lib/api/client";
import i18n from "@/i18n";
import { AlertsPage } from "@/features/alerts/AlertsPage";
import { ErrorsPage } from "@/features/errors/ErrorsPage";
import { InstallationsPage } from "@/features/installations/InstallationsPage";

/**
 * Screens rendered against payloads shaped the way the API actually shapes them.
 *
 * This suite exists because of a specific failure, not as a formality. Three
 * screens were written against fixture shapes the control plane never produced —
 * a job "target" string, an alert "reference", an installation
 * "lastTransitionAt" — and every test passed while one of them crashed on load
 * against a real server. Fixtures agreed with the client because the same person
 * wrote both.
 *
 * So the payloads below are copied from the contracts, not from the fixtures,
 * and the assertion that matters most is the dull one: the screen renders its
 * data without throwing.
 */

beforeEach(() => {
  setAccessToken("test-token");
  vi.unstubAllGlobals();
});

describe("AlertsPage", () => {
  const alert = {
    id: "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
    source: "Server",
    sourceId: "3f2504e0-4f89-11d3-9a0c-0305e82c3302",
    customerId: null,
    severity: "Critical",
    ruleKey: "server.offline",
    message: "web-01 has not reported for 12 minutes.",
    raisedAt: new Date().toISOString(),
    resolvedAt: null,
    acknowledgedAt: null,
    occurrenceCount: 12,
    lastObservedAt: new Date().toISOString(),
    isOpen: true,
  };

  it("renders an alert from the shape the API returns", async () => {
    mockApi({ "/monitoring/alerts": paged([alert]) });

    renderScreen(<AlertsPage />, { permissions: ["server.manage"] });

    // Every row is rendered twice — once as a table row, once as a card for
    // narrow screens — so the assertion is on presence, not on count.
    expect((await screen.findAllByText("server.offline")).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/web-01/).length).toBeGreaterThan(0);
  });

  it("shows the occurrence count rather than one row per occurrence", async () => {
    // An alert is deduplicated by rule and source: a six-hour outage is one row
    // with a count, and a screen that lost the count would bury it.
    mockApi({ "/monitoring/alerts": paged([alert]) });

    renderScreen(<AlertsPage />, { permissions: ["server.manage"] });

    await screen.findAllByText("server.offline");

    // One alert, however many times its condition was observed: the count is a
    // number on the row, not a row of its own.
    expect(screen.getAllByText("12").length).toBeGreaterThan(0);
  });

  it("offers no actions without the permission", async () => {
    mockApi({ "/monitoring/alerts": paged([alert]) });

    renderScreen(<AlertsPage />, { permissions: [] });

    await screen.findAllByText("server.offline");

    // Permissions drive what the UI offers; the API still enforces the rule.
    expect(screen.queryByRole("button", { name: i18n.t("alerts.resolve") })).not.toBeInTheDocument();
  });
});

describe("ErrorsPage", () => {
  const group = {
    id: "3f2504e0-4f89-11d3-9a0c-0305e82c3311",
    storeId: "3f2504e0-4f89-11d3-9a0c-0305e82c3312",
    storeName: "cafe1.ir",
    environment: "Production",
    exceptionType: "IntegrityError",
    title: "IntegrityError: duplicate key value violates unique constraint {value}",
    endpoint: "/api/orders/{id}/items",
    occurrenceCount: 21,
    status: "New",
    firstSeenAt: new Date().toISOString(),
    lastSeenAt: new Date().toISOString(),
    firstSeenVersion: "4.2.0",
    lastSeenVersion: "4.3.0",
    isRegression: true,
    incidentId: null,
  };

  it("renders a group and marks a regression", async () => {
    mockApi({ "/errors/groups": paged([group]) });

    renderScreen(<ErrorsPage />, { permissions: ["errors.manage"] });

    expect((await screen.findAllByText("IntegrityError")).length).toBeGreaterThan(0);

    // A fix that did not hold is not the same as a new problem, and the row has
    // to say so.
    // Asserted through the translation table rather than a literal, so the test
    // says what it means and does not break when the wording is improved.
    expect(screen.getAllByText(i18n.t("errors.regression")).length).toBeGreaterThan(0);
  });

  it("shows the templated route, not a concrete one", async () => {
    mockApi({ "/errors/groups": paged([group]) });

    renderScreen(<ErrorsPage />, { permissions: [] });

    expect((await screen.findAllByText("/api/orders/{id}/items")).length).toBeGreaterThan(0);
  });

  it("survives a group with no endpoint and no version", async () => {
    // Not every error happens on a request, and a store may not report its
    // version. Both are nullable in the contract and neither may crash a row.
    mockApi({
      "/errors/groups": paged([
        { ...group, endpoint: null, firstSeenVersion: null, lastSeenVersion: null, isRegression: false },
      ]),
    });

    renderScreen(<ErrorsPage />, { permissions: [] });

    expect((await screen.findAllByText("IntegrityError")).length).toBeGreaterThan(0);
  });
});

describe("InstallationsPage", () => {
  const installation = {
    id: "3f2504e0-4f89-11d3-9a0c-0305e82c3321",
    storeId: "3f2504e0-4f89-11d3-9a0c-0305e82c3322",
    storeName: "cafe1.ir",
    featureId: "3f2504e0-4f89-11d3-9a0c-0305e82c3323",
    featureName: "Analytics core",
    featureSlug: "knight-feature-analytics-core",
    entitled: true,
    isEnabled: true,
    installedVersion: "1.2.3",
    targetVersion: "1.2.3",
    previousVersion: null,
    state: "Installed",
    health: "Healthy",
    currentJobId: null,
    failureCode: null,
    failureMessage: null,
    rollbackOutcome: "NotAttempted",
    blockingReason: null,
    requiresManualIntervention: false,
    installedAt: new Date().toISOString(),
    disabledAt: null,
    lastTransitionAt: new Date().toISOString(),
  };

  const job = {
    id: "3f2504e0-4f89-11d3-9a0c-0305e82c3331",
    storeId: installation.storeId,
    storeName: "cafe1.ir",
    featureId: installation.featureId,
    featureSlug: "knight-feature-analytics-core",
    type: "Install",
    state: "Running",
    targetVersion: "1.2.3",
    trigger: "Manual",
    completedStepCount: 4,
    totalStepCount: 9,
    attemptCount: 1,
    maxAttempts: 3,
    failureCode: null,
    failureMessage: null,
    rollbackOutcome: "NotAttempted",
    queuedAt: new Date().toISOString(),
    claimedAt: new Date().toISOString(),
    completedAt: null,
    correlationId: "0HMV9A2C41",
  };

  it("renders installations without crashing on the real shape", async () => {
    // The regression this whole suite exists for: this screen threw on load
    // because it formatted a date the API had never sent.
    mockApi({ "/installations": paged([installation]), "/jobs": paged([job]) });

    renderScreen(<InstallationsPage />, { permissions: ["installation.manage"] });

    expect((await screen.findAllByText("Analytics core")).length).toBeGreaterThan(0);
  });

  it("shows entitlement and installation as separate facts", async () => {
    // The column this screen exists for: a capability that is paid for and not
    // running must be visible, not hidden behind one status.
    mockApi({
      "/installations": paged([
        { ...installation, entitled: true, state: "NotInstalled", installedVersion: null },
      ]),
      "/jobs": paged([]),
    });

    renderScreen(<InstallationsPage />, { permissions: [] });

    await screen.findAllByText("Analytics core");

    expect(screen.getAllByText(i18n.t("installations.entitled")).length).toBeGreaterThan(0);
  });

  it("renders a job's progress from the counts the API sends", async () => {
    mockApi({ "/installations": paged([]), "/jobs": paged([job]) });

    renderScreen(<InstallationsPage />, { permissions: ["job.manage"] });

    // The jobs tab has to be selected before its table renders. The filter tabs
    // are exposed as tabs, not buttons, which is what a screen reader expects.
    (await screen.findAllByRole("tab", { name: new RegExp(i18n.t("installations.tabJobs")) }))[0]!.click();

    await waitFor(() => expect(screen.getAllByText("4/9").length).toBeGreaterThan(0));
  });
});
