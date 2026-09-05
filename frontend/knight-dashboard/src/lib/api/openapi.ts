import type { paths, components } from "./schema";

/**
 * The API contract, generated from KNIGHT's OpenAPI document — the source of
 * truth for what every endpoint accepts and returns.
 *
 * `schema.ts` is generated (`npm run gen:api-types`) from the committed snapshot
 * `openapi.json`, refreshed from a running API with `npm run snapshot:api`. It is
 * exported here under readable names so screens can adopt spec-derived types
 * incrementally instead of the hand-written ones in `domain.ts`, closing the gap
 * a hand-written type once hid — a response shape the API had changed, which the
 * client kept reading in the old shape and silently dropped (phase 10).
 */
export type ApiPaths = paths;
export type ApiComponents = components;

/** The named request-body and enum schemas, keyed by their OpenAPI name. */
export type ApiSchemas = components["schemas"];

/**
 * The query parameters a `GET` on `Path` accepts, from the contract.
 *
 * Response *bodies* are not yet in the document — the minimal-API endpoints
 * return `Results.Ok(...)` without a typed `Produces<T>`, so the generator has no
 * response schema to emit. Typing responses from the spec is a follow-up that
 * belongs on the backend (annotate each endpoint's success type); until then the
 * generated types cover query parameters and request bodies, which is where a
 * wrong shape leaves the client rather than the server.
 */
export type Query<Path extends keyof paths> = paths[Path] extends {
  get: { parameters: { query?: infer Q } };
}
  ? Q
  : never;
