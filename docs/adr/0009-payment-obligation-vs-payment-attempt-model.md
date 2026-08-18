# ADR 0009: Payment Obligation vs Payment Attempt Model

## Status
Accepted

## Context
Multi-tenant commerce requires robust financial processing that handles payment obligations, retries across failed external provider interactions, partial network failures, and diverse settlement methods (online gateways and pay-on-fulfillment). To ensure financial integrity, order lifecycles and payment lifecycles must remain decoupled, payment amounts must be server-authoritative and immutable once created, and multiple competing payment obligations for a single order must be prevented.

## Decisions

1. **Strict Architectural Separation (`Order ≠ Payment ≠ Checkout`)**:
   - `Ordering.Domain` owns order items, totals, fulfillment snapshot, and order lifecycle.
   - `Payment.Domain` owns the financial payment obligation (`Payment`), settlement execution attempts (`PaymentAttempt`), status histories, and provider-neutral ports.
   - `Payment.Domain` does not depend on `Ordering.Domain`, and `Ordering.Domain` does not depend on `Payment.Domain`.
   - Cross-module order resolution occurs via the `IPaymentOrderReader` application port implemented in `Knight.Infrastructure`.
   - Payment status transitions (e.g., `Succeeded` or `Cancelled`) do not directly mutate `Order.Status`.

2. **One Payment Obligation per Order & Attempt Retry Pattern**:
   - Each order has at most **one** active `Payment` aggregate (`unique (TenantId, OrderId)` in PostgreSQL).
   - Retrying failed external provider interactions is modeled via sequential `PaymentAttempt` records (`unique (TenantId, PaymentId, AttemptNumber)`) under the existing `Payment` aggregate rather than creating duplicate competing payment obligations.
   - Historical failed attempts remain immutable records.

3. **Server-Authoritative Historical Amount Snapshot**:
   - Clients never specify authoritative payment amounts or currencies.
   - Upon payment creation, `Payment.Amount` and `Payment.Currency` are snapshotted server-side from `Order.Total` and `Order.Currency`.
   - Subsequent changes to catalog prices or delivery fees have zero impact on existing payments.

4. **Settlement Rules & Provider Neutrality**:
   - **PayOnFulfillment**: Payment obligation settled manually by authorized tenant staff (`payments.status.manage`) upon delivery or pickup completion.
   - **Online**: Payment settlement is strictly provider-driven. Manual status overrides by staff are prohibited (`ConflictException` / `ValidationException`) to prevent financial fabrication.
   - Provider interactions are abstracted behind `IPaymentProvider` and `IPaymentProviderResolver`. Zero provider-specific SDKs, credentials, or public webhook routes are exposed in the core platform.

5. **PCI-Free Financial Security**:
   - No primary account numbers (PAN), CVV, expiry dates, or PINs are accepted, processed, or persisted.
   - Provider references are treated as opaque, non-secret identifiers with tenant-scoped uniqueness (`unique (TenantId, ProviderKey, ProviderReference) WHERE "ProviderReference" IS NOT NULL`).

## Consequences

- **Positive**: Complete financial traceability, strong concurrency guarantees against duplicate charges or contradictory states, zero vendor lock-in, clean multi-tenant isolation, and strict module boundaries.
- **Trade-offs**: Retries must always target the existing payment obligation rather than creating a new payment resource.
