> **LEGACY DOCUMENT.** This describes the previous product (a shared
> multi-tenant food-service SaaS), not KNIGHT's target control-plane
> architecture. See [`docs/README.md`](../README.md) and
> [`docs/adr/0010`](../adr/0010-pivot-to-control-plane.md). Kept because it
> documents code that still exists in `backend/`.

# Fulfillment & Delivery Architecture

## 1. Core Architectural Principle

The platform maintains a strict separation of concerns across three business concepts:

```text
Order ≠ Fulfillment ≠ Delivery
```

- **Ordering Module (`modules/Ordering`)**:
  - Owns the order lifecycle, state machine, product/modifier price snapshots, party snapshots, totals calculation, and the immutable historical `OrderFulfillmentSnapshot`.
  - Financial formula: $\text{Total} = \text{Subtotal} + \text{FulfillmentFee}$.
  - Has zero direct dependency on `Delivery.Domain` or `Fulfillment.Domain`.
- **Fulfillment Module (`modules/Fulfillment`)**:
  - Owns fulfillment method semantics (`Pickup` vs `Delivery`).
  - Owns tenant operational pickup enablement (`TenantFulfillmentSettings.PickupEnabled`).
  - Operates independently of the `delivery` feature: disabling delivery does not disable pickup.
- **Delivery Module (`modules/Delivery`)**:
  - Owns the `delivery` feature capability entitlement (`DeliveryFeature.Key = "delivery"`).
  - Owns tenant delivery operational settings (`TenantDeliverySettings.IsAcceptingDeliveryOrders`, `DefaultMinimumOrderSubtotal`).
  - Owns delivery pricing zones (`DeliveryZone`).
  - Owns delivery quote calculation (`IDeliveryQuoteService`).

---

## 2. Capability Feature vs Operational State

Platform entitlement and tenant operational preferences are decoupled:

| Feature State (`delivery`) | Operational State (`IsAcceptingDeliveryOrders`) | Delivery Placement Result |
| :--- | :--- | :--- |
| `Disabled` | Any | **Blocked** (`400 Validation`) |
| `Enabled` | `False` | **Blocked** (`400 Validation`) |
| `Enabled` | `True` | **Allowed** (if zone and subtotal valid) |

Pickup is decoupled from delivery:
- `delivery` feature is **not** required for Pickup fulfillment.
- Pickup is operationally controlled by `TenantFulfillmentSettings.PickupEnabled` (default `true`).
- For Pickup: `FulfillmentFee = 0.00`.

---

## 3. Server-Authoritative Pricing & Minimums

Clients and callers are never trusted to provide fees or subtotal validation:
1. When placing a delivery order with `DeliveryZoneId`, the server resolves the `DeliveryZone` record for the tenant.
2. The server verifies that `DeliveryZone.Status == Active`.
3. The server computes the effective minimum subtotal using deterministic precedence:
   $$\text{EffectiveMinimum} = \text{Zone.MinimumOrderSubtotal} \mathbin{??} \text{TenantSettings.DefaultMinimumOrderSubtotal} \mathbin{??} \text{None}$$
4. The server validates that $\text{OrderSubtotal} \ge \text{EffectiveMinimum}$. The delivery fee itself does not contribute toward meeting the minimum subtotal.
5. The server freezes `Zone.Fee` into `OrderFulfillmentSnapshot.FulfillmentFee` and calculates `Order.Total = Subtotal + FulfillmentFee`.

---

## 4. Historical Snapshot Immutability

Historical orders must remain completely independent of future operational or configuration changes:
- `OrderFulfillmentSnapshot` stores:
  - `Method` (`Pickup` or `Delivery`)
  - `FulfillmentFee` (frozen monetary value)
  - `DeliveryZoneId` (traceability UUID only; **no foreign key constraint** to `delivery_zones`)
  - `DeliveryZoneName` (frozen display name at time of order)
  - `AddressLine1`, `AddressLine2`, `City`, `PostalCode`, `Latitude`, `Longitude`
- Subsequent actions (renaming a delivery zone, changing a zone fee, archiving a zone, disabling delivery orders, or turning off the tenant's `delivery` feature) have **zero effect** on existing historical orders.
- Order detail reads load the snapshot from `order_fulfillment_snapshots` and make **no live joins** to `delivery_zones` or settings tables.

---

## 5. Privacy & Address PII

- Delivery addresses are sensitive customer information.
- Raw address objects and full address lines are **not** logged.
- `OrderSummaryResponse` (order list view) exposes only `FulfillmentMethod` and does not leak address fields.
- Full address details are returned only in authorized single-order detail responses (`OrderDetailResponse.Fulfillment.Delivery`).
- Audit logging records only operational flags and entity IDs; raw address lines are never included in audit metadata.

---

## 6. Deferred Capabilities

The following capabilities are explicitly deferred:
- Geographic polygon resolution, geocoding, PostGIS, routing APIs, and map providers.
- Persistent saved customer addresses.
- Public checkout and cart orchestration.
- Driver accounts, courier assignment, ETA prediction, and real-time delivery tracking.
- Multi-branch delivery zones and inventory management.
