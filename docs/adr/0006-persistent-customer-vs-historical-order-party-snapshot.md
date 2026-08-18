# ADR 0006: Persistent Customer vs Historical Order Party Snapshot

## Context
When an order is placed, it captures the identity and contact details of the purchasing party. Over time, customers may update their profile (name, phone, email), be archived, or request privacy anonymization under data protection regulations (such as GDPR).

If historical orders dynamically joined current customer records:
1. Past orders would retroactively show modified names or contact details, corrupting historical business integrity and invoice compliance.
2. Deleting or archiving a customer would cascade-delete or break historical orders.
3. Guest orders without a persistent customer record would require synthetic customer entities or awkward nullable relational joins.

## Decision
1. **Module Ownership**:
   - The `Customer` module owns persistent customer profiles and CRM state (`customers` table).
   - The `Ordering` module owns the frozen `OrderPartySnapshot` aggregate entity (`order_party_snapshots` table).
2. **Decoupled Cross-Module Integration**:
   - `Ordering.Domain` defines `ICustomerOrderingReader`.
   - `Knight.Infrastructure` implements the adapter to read customer data for placement snapshots.
   - Zero database foreign keys exist from `order_party_snapshots` to `customers`.
3. **Immutability**:
   - Once created at placement time, `OrderPartySnapshot` is immutable.
   - Editing or archiving a customer does not alter existing order party snapshots.
4. **Feature Independence**:
   - The `customers` tenant feature flag controls access to `/api/tenant/customers` CRM APIs only.
   - Historical order inspection and guest snapshots are independent of the `customers` feature flag.

## Consequences
- **Positive**: Historical order integrity is guaranteed; historical queries never join current customer tables; cross-module dependencies are strictly decoupled.
- **Trade-off**: Customer data is duplicated in the snapshot at placement time (storage trade-off in favor of auditability and domain isolation).
