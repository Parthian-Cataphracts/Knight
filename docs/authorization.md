# Authorization

Status: **authoritative**. Supersedes `architecture/authorization.md`.

Two independent checks must pass for every request:

```
1. Permission check     does this principal hold the required permission?
2. Isolation check      does the target resource belong to this principal's customer?
```

Passing one is never sufficient. The frontend performs neither — it only hides
what the user cannot use.

## 1. Roles

Roles are data, not code. Seeded defaults:

| Role | Scope | Intent |
|---|---|---|
| `SuperAdmin` | Platform | Everything, including role and plan definition |
| `Admin` | Platform | Day-to-day operation of all customers |
| `Developer` | Platform | Monitoring, errors, logs, incidents, deployments; no billing |
| `Support` | Platform | Read-mostly across customers; incident notes |
| `CustomerOwner` | Customer | Full access to their own customer, including subscription |
| `CustomerStaff` | Customer | Read-mostly access to their own customer's stores and errors |

Custom roles can be created; system roles cannot be deleted.

## 2. Permissions

Granular, dot-namespaced keys:

```
customer.view      customer.create    customer.update    customer.archive
store.view         store.create       store.manage       store.credentials.manage
plan.view          plan.manage
feature.view       feature.manage     feature.publish    feature.yank
installation.view  installation.manage    installation.uninstall
installation.rollback                     job.view       job.manage
subscription.view  subscription.manage
billing.view       billing.manage
server.view        server.manage      agent.manage
monitoring.view    logs.view          logs.export
errors.view        errors.manage      incident.view      incident.manage
notification.manage
audit.view         report.view
user.view          user.manage        role.view          role.manage
```

The ingestion principals hold narrow internal permissions: `ingest.write`
(stores) and `agent.report` + `agent.executeJob` (agents, scoped to their own
store).

Feature-lifecycle permissions are deliberately split by blast radius:

| Permission | Who | Why |
|---|---|---|
| `feature.manage` | Platform staff | edit registry metadata |
| `feature.publish` / `feature.yank` | SuperAdmin / release role | publishing ships executable code to every entitled store |
| `installation.manage` | Platform staff; `CustomerOwner` for their own store | install, upgrade, enable, disable, configure |
| `installation.uninstall` | Platform staff only | removes code and eventually data |
| `installation.rollback` | Platform staff | recovery operation |

## 3. Enforcement layers

```
Endpoint policy        RequirePermission("store.manage")   -> 403 with code "forbidden"
Application service    re-checks intent for non-trivial operations
Persistence            customer scoping applied as a global query filter
```

The persistence filter is the safety net: a forgotten `where customerId = ...`
in a handler must not become a data leak. Platform-scoped principals bypass the
filter only through an explicit, audited escape hatch.

## 4. Isolation invariant

```
Customer A  ──X──>  Store B, Server B, Subscription B, Invoice B,
                    ErrorGroup B, LogEntry B, Incident B, User B
```

A customer-scoped principal requesting a resource of another customer receives
`404`, not `403`, so existence is not disclosed. This must be covered by
integration tests, not assumed.

## 5. Feature entitlement vs permission

They are different and both required:

```
permission   "may this user perform this action?"
entitlement  "has this customer paid for this capability?"
```

An endpoint gated by a feature returns `403` with code `feature_not_entitled`
and the missing feature key. Entitlement is never evaluated from a client-sent
value.

A third axis exists on the store side: **installation**. Entitled but not
installed means the capability is unavailable, and that is a delivery problem
to surface — not a `403` to hide. Installed but not entitled means the code is
present and must refuse to serve
([`feature-delivery.md`](feature-delivery.md) §2).

## 6. Mandatory security tests

These are release-blocking (`tests/Knight.IntegrationTests/Security`):

- Customer A cannot read or mutate any Customer B resource (per resource type).
- A customer principal cannot call platform-only endpoints.
- A customer cannot change their own subscription without `subscription.manage`.
- A customer cannot enable a feature that is not `isCustomerToggleable`, nor one
  requiring dedicated infrastructure on shared hosting.
- Expired, revoked, or wrong-environment store credentials are rejected.
- A store token cannot access dashboard endpoints; an agent token cannot access
  ingestion endpoints; a user token cannot access agent endpoints.
- A store cannot ingest data attributed to another store.
- Removing a role or permission takes effect on the next request, not the next
  login.
- A customer cannot install, upgrade, or uninstall a Feature they are not
  entitled to, nor target another customer's store.
- A customer principal cannot publish or yank a Feature version.
- An agent token can claim and report only jobs belonging to its own store.
- A store/agent cannot read another store's installation state or jobs.
- An unsigned or digest-mismatched artifact is refused by the agent (tested in
  the integration suite).

## 7. Auditing

Every state-changing administrative action writes an `AuditLog` row with actor,
action, target, before/after values, correlation id, and IP. Read access to
sensitive data (logs export, credential issue) is also audited.

Feature lifecycle events are audited with their full context — customer, store,
feature, version, actor **or automation source**, result, and correlation id:

```
FeaturePublished · FeatureVersionYanked · FeatureInstalled · FeatureUpgraded
FeatureInstallationFailed · FeatureRolledBack · FeatureDisabled
FeatureUninstalled · FeatureDataPurged · FeatureConfigurationChanged
```

Configuration values that the manifest marks as secrets are recorded as
`"***"` with only the key name and a change indicator.
