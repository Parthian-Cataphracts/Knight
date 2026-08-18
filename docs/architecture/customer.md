# Customer Core & CRM Architecture

## 1. Identity Separation

The platform distinguishes three separate human and business identities:

| Identity | Role | Auth Principal? | Credentials / JWT | Permissions |
| :--- | :--- | :--- | :--- | :--- |
| **PlatformAdmin** | Platform operator | Yes | Yes (email/password, JWT) | Global platform claims |
| **TenantUser** | Tenant staff/admin | Yes | Yes (email/password, JWT) | Tenant roles/permissions |
| **Customer** | End customer | **No** (Phase 06) | **None** (No passwords, tokens) | None |

Customer records in Phase 06 are domain entities representing tenant business clients for CRM and ordering tracking. They are not authentication principals and do not participate in JWT authentication flows.

---

## 2. Customer Module Boundary

The `Customer` module owns:
- Persistent tenant-scoped customer aggregate root (`Customer`)
- Contact normalization and validation (conservative phone normalization, lowercase RFC email validation)
- Customer lifecycle (`Active` $\rightarrow$ `Archived` $\rightarrow$ `Active`)
- Tenant and Platform administrative APIs
- Feature declaration (`customers`) and permissions (`customers.view`, `customers.create`, `customers.update`, `customers.archive`, `customers.restore`)

The Customer module has zero dependency on `Ordering`, `Checkout`, `Delivery`, `Payments`, or `Loyalty`.

---

## 3. Persistent Customer vs. Historical Order Party Snapshot

Ordering historical data must remain completely independent of future customer edits, archival, or privacy redactions.

1. **Ordering Owned Snapshot**: The `Ordering` module owns `OrderPartySnapshot`, storing historical name, phone, and email frozen at placement time.
2. **Cross-Module Read Port**: During order placement, `Ordering` accesses customer data via `ICustomerOrderingReader` (implemented in Infrastructure).
3. **No Cross-Module Foreign Keys**: `OrderPartySnapshot` references `SourceCustomerId` as a traceability field, with no relational FK to the `customers` table.
4. **Feature Independence**: The `customers` feature controls access to the CRM endpoints (`/api/tenant/customers`). Guest checkout and historical order inspection remain functional even when the `customers` feature is disabled.

---

## 4. Privacy & PII Handling

1. **No Sensitive PII in Logs**: Loggers never emit full request payloads, raw phones, or raw emails.
2. **Lightweight Order Summaries**: The order list summary returns only `CustomerDisplayName` to prevent PII over-exposure. Full contact details are restricted to single-order detail endpoints.
3. **Safe Auditing**: Audit entries record the actor, action, timestamp, tenant, entity ID, and non-PII flags (e.g. `hasPhone`, `hasEmail`), without storing raw contact strings.
