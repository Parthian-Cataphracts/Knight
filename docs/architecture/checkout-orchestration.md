# Checkout Orchestration Architecture

## 1. Overview & Conceptual Boundary

The `Checkout` module (`modules/Checkout/`) serves as the platform's public, storefront-facing orchestration layer. It unites:
- Guest Contact (`GuestParty`)
- Catalog Item and Modifier Selection
- Fulfillment and Delivery Resolution
- Server-Authoritative Ordering
- Database-backed Transactional Idempotency

### Core Rule: Orchestration Only
Checkout coordinates domain operations across modules without owning or duplicating domain business rules:
- **Product orderability, variant rules, modifier bounds, pricing**: Owned by `Catalog` & `Ordering`.
- **Fulfillment & delivery fee calculation**: Owned by `Fulfillment` & `Delivery`.
- **Order persistence, item snapshots, party snapshot, status lifecycle**: Owned by `Ordering`.

```text
Storefront Request (Anonymous + Idempotency-Key)
                  ↓
       Checkout Orchestration
  ┌───────────────┴───────────────┐
  ↓                               ↓
Idempotency Claim           Shared Pricing Pipeline
(TenantId + KeyHash)       (IOrderPricingCalculator)
                                  ↓
                        Ordering Order Placement
                                  ↓
                        Idempotency Completion
```

---

## 2. Shared Pricing & Validation Engine

To guarantee that advisory checkout quotes (`POST /api/public/checkout/quote`) and final order submissions (`POST /api/public/checkout/orders`) use 100% identical validation and pricing rules:
- `Ordering` exposes [`IOrderPricingCalculator`](../../modules/Ordering/Domain/IOrderPricingCalculator.cs).
- Both `OrderPlacementService.PlaceOrderAsync` and `CheckoutOrderingGateway.CalculateQuoteAsync` execute through this single engine.
- Quotes are strictly advisory (0 database rows written, 0 order numbers allocated).
- At final order placement, everything is re-resolved server-side against current database state.

---

## 3. Public Storefront Security Policy

1. **Anonymous with Tenant Resolution**:
   - Resolved via Host header (`ITenantContext`).
   - Unknown host fails closed with 404 Not Found.
2. **CustomerId Prohibited Publicly**:
   - Public checkout does not accept `CustomerId` until secure customer authentication exists.
   - `GuestParty` (Name + Phone and/or Email) is mandatory for storefront checkout.
3. **Feature Gating**:
   - Requires `ordering` and `catalog` features enabled.
   - Delivery fulfillment additionally requires `delivery` feature and `IsAcceptingDeliveryOrders = true`.
   - Pickup remains operational even when `delivery` feature is disabled.
   - Guest checkout operates independently of the `customers` CRM feature.

---

## 4. Transactional Idempotency & Concurrency

1. **Key Hashing & Canonical Request Fingerprinting**:
   - `KeyHash = SHA256(trimmed(Idempotency-Key))` (64 lowercase hex characters).
   - `RequestHash = SHA256(canonical(GuestParty, Items, Fulfillment))` (64 lowercase hex characters).
2. **PostgreSQL Unique Constraint**:
   - Table `checkout_idempotency_records` with unique constraint `(TenantId, KeyHash)`.
   - Different tenants may use the same raw key without collision.
3. **Atomic Transaction Scope**:
   - Single database transaction encapsulates key claim, order placement, snapshots, status history, and completion timestamp.
   - Failed validation rolls back completely, leaving the key free for a corrected retry.
4. **Replay vs Conflict Semantics**:
   - Same key + same payload $\rightarrow$ `200 OK` replay with identical `OrderId` and `OrderNumber`.
   - Same key + different payload $\rightarrow$ `409 Conflict`.
   - Same payload + different keys $\rightarrow$ creates 2 distinct orders.

---

## 5. Rate Limiting & Privacy

- Dedicated policies `"checkout_quote"` and `"checkout_submit"` partitioned per storefront and caller, by `(RequestHost, ClientIp)`.
  The host stands in for the tenant here: rate limiting runs ahead of `TenantResolutionMiddleware` (so a flood costs no
  tenant-resolution database lookup), which means `ITenantContext` is not yet populated when the partition key is built.
  Distinct tenants own distinct hosts, so one storefront still cannot consume another's budget.
- Zero logging or audit storage of raw idempotency keys or contact/address PII.
