import { describe, it, expect } from "vitest";
import type { Query } from "../openapi";
import spec from "../openapi.json";

/**
 * The generated API contract, and a guard against it drifting away from what the
 * dashboard actually calls.
 *
 * The committed snapshot (`openapi.json`) is refreshed from a running API with
 * `npm run snapshot:api` and the types regenerated with `npm run gen:api-types`.
 * The class of bug this guards is the one phase 10 found: an endpoint the API
 * renamed or dropped that the client kept calling, discovered only in a browser.
 */
describe("the OpenAPI contract snapshot", () => {
  const paths = (spec as { paths: Record<string, unknown> }).paths;

  it("carries paths and named schemas", () => {
    expect(Object.keys(paths).length).toBeGreaterThan(0);
    expect(Object.keys((spec as { components: { schemas: object } }).components.schemas).length).toBeGreaterThan(0);
  });

  it.each([
    "/api/v1/logs",
    "/api/v1/logs/export",
    "/api/v1/customers",
    "/api/v1/stores",
    "/api/v1/provisioning",
    "/api/v1/installations",
    "/api/v1/plans",
    "/api/v1/jobs",
  ])("still exposes %s, which a screen depends on", (path) => {
    expect(paths).toHaveProperty(path);
  });
});

// A type-level check, not a runtime one: the log stream's contract must keep the
// severity and search filters the screen sends. If the API renames or drops one,
// this stops compiling — the drift is caught by `tsc`, before any browser.
type LogsQuery = Query<"/api/v1/logs">;
const _filtersExist: (query: LogsQuery) => unknown = (query) => [
  query?.minSeverity,
  query?.search,
  query?.from,
  query?.to,
];
void _filtersExist;
