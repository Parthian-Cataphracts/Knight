# ADR 0008: Transactional Checkout Idempotency and Quote Orchestration

## Status
Accepted

## Context
Phase 08 introduces the first public, anonymous customer-facing order submission workflow. In distributed web and storefront environments:
1. Network timeouts, user double-clicks, and automated client retries can result in duplicate order placements and duplicate financial commitments.
2. In-memory locks or single-process synchronization mechanisms fail in multi-replica / multi-instance production environments.
3. Pre-checkout pricing quotes must not diverge from final order placement calculations while remaining strictly advisory (non-persisted).
4. Unauthenticated callers must not be allowed to claim or spoof persistent customer CRM records (`CustomerId`).

## Decision
1. **Module & Port/Adapter Architecture**:
   - Establish `modules/Checkout/` as an orchestration layer communicating with `Ordering` via `ICheckoutOrderingGateway`.
   - Extract `IOrderPricingCalculator` in `Ordering` to share identical pricing and validation logic between `Quote` and `PlaceOrder`.
2. **Deterministic Cryptographic Idempotency**:
   - Store `KeyHash = SHA256(rawIdempotencyKey)` (64-char hex) and `RequestHash = SHA256(canonicalPayload)` (64-char hex) in table `checkout_idempotency_records`.
   - Enforce database-level unique index on `(TenantId, KeyHash)`.
3. **Atomic Transactional Boundary**:
   - Execute key claim, catalog orderability checks, modifier calculations, fulfillment fee resolution, party snapshot creation, order persistence, and completion timestamp within a single PostgreSQL transaction.
   - Any validation failure rolls back the transaction completely, releasing the key for immediate retry with a corrected payload.
4. **Replay & Conflict Semantics**:
   - First submission: `201 Created`.
   - Idempotent replay (same key, matching request fingerprint): `200 OK` returning original order data without duplicate `OrderPlaced` audit events.
   - Key reuse with different payload: `409 Conflict`.
5. **Storefront Security & Privacy**:
   - Prohibit `CustomerId` in public contracts; require `GuestParty`.
   - Partition rate limiting per storefront and caller, by `(RequestHost, ClientIp)` — the host is the tenant signal
     available before tenant resolution runs, so no tenant can exhaust another tenant's budget.
   - Never log raw idempotency keys or address PII.

## Consequences
- Multi-instance safe idempotency without distributed locks or external caching dependencies.
- Zero duplicate orders under concurrent network retries.
- Zero price divergence between quotes and final orders.
- Long-term idempotency record retention cleanup is deferred to future maintenance jobs.
