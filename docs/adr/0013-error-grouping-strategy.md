# 0013 — Error fingerprinting and grouping strategy

- Status: **Proposed**
- Date: 2026-08-18

## Context

Stores report errors continuously. Storing each occurrence as a distinct
incident is explicitly called out as wrong: 100 identical errors are one
problem. Operators need "what is broken, since when, how often", not a firehose.

## Options considered

1. **No grouping** — unusable and unbounded storage growth.
2. **Group by exception type only** — collapses unrelated failures that share a
   common exception class (e.g. `IntegrityError` everywhere).
3. **Group by full stack-trace hash** — too granular; line-number shifts between
   versions fragment one problem into many groups.
4. **Normalised fingerprint over a stable subset of signals.**

## Decision

**Option 4.**

```
fingerprint = sha256(storeId | environment | exceptionType |
                     normalisedStackTop | endpointTemplate)
```

- `normalisedStackTop` — the top N in-application frames (vendor/framework
  frames stripped), with file paths relativised, line numbers dropped, and
  numeric/uuid literals replaced by placeholders.
- `endpointTemplate` — the route template (`/api/orders/{id}/`), never the
  concrete URL.
- `version` is **not** in the fingerprint (so a problem persists across
  deployments) but is recorded per event and shown as "first seen in version X".

Ingestion upserts the `ErrorGroup` (incrementing `occurrenceCount`, updating
`lastSeenAt`) and appends a bounded `ErrorEvent` sample. A resolved group that
recurs is reopened and marked as a regression.

Incidents are separate: a group may raise an incident via a rule (spike, new
critical 5xx in Production, sustained rate), and many groups may attach to one
incident.

## Consequences

**Positive** — bounded, readable error lists; stable identity across
deployments; incidents stay meaningful; storage is dominated by counters, not
payloads.

**Negative** — normalisation is language-specific (a Django/Python normaliser
first) and imperfect: over-grouping and under-grouping both occur. Mitigation:
store the raw stack on sampled events, allow manual merge/split of groups, and
version the fingerprint algorithm (`fingerprintVersion` on the group) so it can
be changed without corrupting history.
