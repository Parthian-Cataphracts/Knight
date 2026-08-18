# ADR 0007: Historical Fulfillment Snapshot & Server-Side Delivery Pricing

## Status
Accepted

## Context
Online orders require fulfillment selection (Pickup or Delivery). Delivery orders require zone-based pricing, minimum order subtotal validation, and recipient addresses. Historical orders must remain financially immutable, resilient against future configuration changes (such as zone renaming, fee adjustments, zone archiving, or tenant feature disabling), and protect customer address PII.

## Decisions

1. **Tri-Boundary Separation**:
   - `Order ≠ Fulfillment ≠ Delivery`.
   - `Ordering.Domain` owns the immutable `OrderFulfillmentSnapshot` and total calculation ($\text{Total} = \text{Subtotal} + \text{FulfillmentFee}$).
   - `Fulfillment` module owns pickup enablement (`TenantFulfillmentSettings`).
   - `Delivery` module owns the `delivery` feature, operational settings, zones, and pricing.
   - Cross-module integration is achieved through the `IOrderFulfillmentResolver` application port, implemented in `Knight.Infrastructure`.

2. **Server-Authoritative Pricing & Minimum Precedence**:
   - Clients never dictate delivery fees or totals.
   - Zone fee is looked up server-side during placement and frozen into the order snapshot.
   - Minimum subtotal follows strict precedence: `DeliveryZone.MinimumOrderSubtotal ?? TenantDeliverySettings.DefaultMinimumOrderSubtotal ?? None`.
   - Validation compares `Order.Subtotal` (sum of items + modifiers) against the effective minimum before applying fulfillment fees.

3. **Decoupled Database Schema & FK Isolation**:
   - `order_fulfillment_snapshots` has a composite FK `(TenantId, OrderId) -> orders(TenantId, Id)` with `Cascade` delete and a unique constraint ensuring 1:1 cardinality.
   - `DeliveryZoneId` is stored as an unconstrained UUID for traceability only. No database foreign key connects `order_fulfillment_snapshots` to `delivery_zones`.
   - Historical orders can be queried even if zones are archived, modified, deleted, or if the `delivery` feature is revoked.

4. **Address Validation & PII Protection**:
   - For delivery orders, `AddressLine1` and `City` are mandatory; coordinates must be provided as paired bounding values ($\text{Latitude} \in [-90, 90]$, $\text{Longitude} \in [-180, 180]$).
   - Address PII is omitted from list/summary endpoints and audit logs, and is returned only in authorized single-order detail responses.

5. **Pickup Decoupled from Delivery**:
   - Pickup orders carry `FulfillmentFee = 0.00` and do not require the `delivery` feature entitlement.

## Consequences

- **Positive**: Clean architectural boundaries, full auditability, financial consistency, zero cross-module database lock-in, and reliable multi-tenant isolation.
- **Trade-offs**: Cross-module resolution happens at the application/infrastructure layer rather than through database joins.
