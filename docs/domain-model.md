# KNIGHT Domain Model

Status: **authoritative** (Phase 1 proposal, open to review before migrations).

All identifiers are `Guid`. All timestamps are UTC. No cross-database foreign
keys ever exist; references to store-side data are opaque identifiers.

## 1. Identity & access

```
User            id, email, displayName, passwordHash, status, mfaEnabled,
                customerId? (null for platform staff), createdAt
Session         id, userId, refreshTokenHash, issuedAt, expiresAt, revokedAt?, ip, userAgent
Role            id, name, scope (Platform | Customer), isSystem
Permission      id, key ("stores.manage"), description
RolePermission  roleId, permissionId
UserRole        userId, roleId, customerId? (customer-scoped assignment)
```

`User.customerId` distinguishes platform staff from customer users. A user
belongs to at most one customer.

## 2. Customers & stores

```
Customer        id, name, legalName?, contactEmail, phone?, status
                (Prospect|Active|Suspended|Archived), createdAt, notes
Store           id, customerId, name, slug, primaryDomain, environment
                (Development|Staging|Production), applicationVersion?,
                integrationStatus (NotRegistered|Pending|Connected|Degraded|Disconnected),
                serverId?, hostingModel, lastSeenAt?, status
                (Provisioning|Active|Suspended|Archived), metadata (jsonb)
StoreCredential id, storeId, clientId, secretHash, createdAt, expiresAt?,
                rotatedAt?, revokedAt?, lastUsedAt?
StoreDeployment id, storeId, version, deployedAt, deployedBy?, notes?, status
```

`Store.metadata` is a deliberately open jsonb bag for infrastructure detail
that does not deserve a column.

## 3. Feature registry (deployable software artifacts)

> A Feature is **versioned, deployable Django functionality**, not a flag.
> See [`feature-delivery.md`](feature-delivery.md) and
> [`adr/0014`](adr/0014-features-as-deployable-packages.md).

```
Feature            id, slug ("knight-feature-analytics"), name, description,
                   category, isOptional, requiresDedicatedInfrastructure,
                   status (Draft|Published|Deprecated|Withdrawn)
FeatureVersion     id, featureId, version (semver), packageReference,
                   artifactDigest (sha256), signature, manifest (jsonb),
                   status (Draft|Published|Yanked), releaseNotes,
                   publishedAt, publishedBy
FeatureDependency  featureVersionId, dependsOnFeatureId, versionRange
FeatureCompatibility  featureVersionId, storeVersionRange, pythonRange, djangoRange
```

`FeatureVersion` is immutable once published; corrections are new versions plus
a yank of the old one. The manifest (`feature-delivery.md` §5) is the contract
and is schema-validated at publish time.

## 4. Plans, subscriptions, entitlements

```
Plan             id, key (basic|custom|professional), name, description,
                 basePrice, currency, isActive, sortOrder
PlanFeature      planId, featureId, isIncluded, isCustomerToggleable,
                 pinnedVersionRange?   (null = latest compatible published)
FeaturePrice     id, featureId, planId?, price, currency, billingPeriod, validFrom, validTo?
Subscription     id, customerId, planId, status (Trial|Active|PastDue|Suspended|Cancelled),
                 startedAt, currentPeriodStart, currentPeriodEnd, cancelledAt?
SubscriptionFeature  subscriptionId, featureId, isEnabled, enabledAt, enabledBy
FeatureEntitlement   id, customerId, featureId, source (Plan|Optional|Grant),
                 grantedAt, expiresAt?, revokedAt?     -- the commercial fact
```

Rules:

- The Basic plan's feature set is data, never code, and is not customer-editable.
- An optional feature is selectable only if `PlanFeature.isCustomerToggleable`.
- Price is always computed from `Plan.basePrice` plus active `FeaturePrice`
  rows; never hard-coded in a controller, service, or React component.
- A feature with `requiresDedicatedInfrastructure` cannot be entitled to a
  store whose `hostingModel` is `SharedManaged`.
- KNIGHT is the single source of truth for entitlements.
- **An entitlement is not an installation.** Granting one triggers delivery; it
  does not by itself make the capability exist in the store.

## 5. Feature installation (technical facts)

```
FeatureInstallation      id, storeId, featureId, installedVersionId?,
                         desiredVersionId?, state (see below), isEnabled,
                         installedAt?, lastTransitionAt, blockingReason?,
                         rollbackOutcome? (None|Succeeded|ManualInterventionRequired),
                         healthStatus, lastHealthCheckAt?
FeatureConfiguration     id, storeId, featureId, values (jsonb), schemaVersion,
                         updatedBy, updatedAt        -- secrets stored encrypted
FeatureInstallationJob   id, storeId, featureId, targetVersionId?, type
                         (Install|Upgrade|ApplyConfiguration|Enable|Disable|
                          Uninstall|Rollback|HealthCheck),
                         status (Queued|Claimed|Running|Succeeded|Failed|Cancelled|TimedOut),
                         plan (jsonb: ordered dependency plan), currentStep,
                         totalSteps, attempt, correlationId, requestedBy,
                         queuedAt, startedAt?, finishedAt?
JobStepResult            id, jobId, stepIndex, name, status, startedAt, finishedAt?,
                         output (truncated, scrubbed), errorCode?, errorDetail?
ProvisioningJob          id, storeId, status, currentStep, totalSteps, ...
                         -- same shape, used by store-provisioning.md
```

`FeatureInstallation.state`:

```
NotInstalled | Pending | Installing | Installed | Updating
             | RollingBack | Failed | Disabled | Uninstalling
```

State transitions are enforced by the aggregate (`feature-delivery.md` §6).
`isEnabled` is tracked separately from `state` so "installed but disabled" is
representable — that is the default outcome of entitlement loss
([`adr/0016`](adr/0016-feature-migration-and-removal-policy.md)).

Job output is truncated and scrubbed before storage; secrets never enter
`JobStepResult` or `FeatureInstallationJob`.

## 6. Billing

```
BillingAccount  id, customerId, currency, billingEmail, taxId?
Invoice         id, customerId, subscriptionId, number, periodStart, periodEnd,
                subtotal, tax, total, currency,
                status (Draft|Issued|Paid|Void|Overdue), issuedAt
InvoiceLine     id, invoiceId, description, featureId?, quantity, unitPrice, total
PaymentRecord   id, invoiceId, amount, method, reference, paidAt, recordedBy
```

KNIGHT records billing facts; it does not process payments in the initial
phases.

## 7. Infrastructure & monitoring

```
Server           id, name, hostingModel, provider?, region?, ipAddress?,
                 environment, status (Unknown|Healthy|Degraded|Offline), lastSeenAt?
Agent            id, serverId, version, tokenHash, status, lastHeartbeatAt?, capabilities (jsonb)
ServerMetric     id, serverId, capturedAt, cpuPercent, memoryUsedBytes, memoryTotalBytes,
                 diskUsedBytes, diskTotalBytes, netInBytes, netOutBytes, loadAvg?
StoreHealthCheck id, storeId, checkedAt, isHealthy, responseTimeMs,
                 dependencies (jsonb: db/redis/worker), version, rawPayload?
Alert            id, source (Server|Store|Agent), sourceId, severity, ruleKey,
                 message, raisedAt, resolvedAt?
```

Metric and health tables are append-only and must have a retention policy from
day one (see `observability.md`).

## 8. Errors & incidents

```
ErrorEvent    id, storeId, occurredAt, environment, version, endpoint?, httpMethod?,
              statusCode?, exceptionType, message, stackTrace?, requestId?, traceId?,
              context (jsonb), errorGroupId
ErrorGroup    id, storeId, fingerprint, exceptionType, endpoint?, title,
              firstSeenAt, lastSeenAt, occurrenceCount,
              status (New|Acknowledged|Resolved|Ignored), assignedToUserId?, incidentId?
Incident      id, customerId, storeId?, serverId?, title, severity,
              status (Open|Investigating|Mitigated|Resolved), openedAt, resolvedAt?, summary?
IncidentEvent id, incidentId, occurredAt, type, actorUserId?, message
```

`fingerprint = hash(storeId, environment, exceptionType, normalisedStackTop, endpoint)`
— see [`adr/0013`](adr/0013-error-grouping-strategy.md).

## 9. Logs, notifications, audit

```
LogEntry            id, storeId?, serverId?, timestamp, level, service, environment,
                    requestId?, traceId?, message, exception?, version, attributes (jsonb)
NotificationChannel id, customerId?, type (Email|Webhook|Sms), target, isActive, secretHash?
Notification        id, channelId, subject, body, relatedType, relatedId, status, sentAt?, error?
AuditLog            id, actorUserId?, actorType (User|System|Store|Agent), action,
                    targetType, targetId, previousValue (jsonb)?, newValue (jsonb)?,
                    correlationId, ipAddress?, occurredAt
```

Audit entries never contain secrets, tokens, or password hashes.

## 10. Relationship overview

```
Customer 1───* Store *───1 Server 1───* Agent
    │              │
    │              ├──* StoreCredential
    │              ├──* StoreDeployment
    │              ├──* StoreHealthCheck
    │              ├──* ErrorGroup ──* ErrorEvent
    │              ├──* FeatureInstallation ──1 FeatureVersion
    │              ├──* FeatureConfiguration
    │              └──* FeatureInstallationJob ──* JobStepResult
    │
    ├──* FeatureEntitlement ──1 Feature 1──* FeatureVersion
    │                                            ├──* FeatureDependency
    │                                            └──1 FeatureCompatibility
    ├──1 Subscription ──* SubscriptionFeature ──1 Feature
    │        └──1 Plan ──* PlanFeature ──1 Feature
    ├──1 BillingAccount ──* Invoice ──* InvoiceLine
    ├──* User ──* UserRole ──1 Role ──* RolePermission ──1 Permission
    └──* Incident ──* IncidentEvent
```

## 11. Isolation invariant

Every query on behalf of a customer-scoped user is filtered by `customerId` at
the persistence layer, not the endpoint layer. Store, server, subscription,
invoice, error, log, and incident access all resolve back to a `customerId`
before any row leaves the database.

## 12. Reuse from the existing codebase

| Target concept | Existing code to adapt |
|---|---|
| `User`, `Session`, tokens | `modules/Identity` |
| `Role`, `Permission` | `modules/Identity` + `Knight.Application/Authorization` |
| `Customer`, `Store` | `modules/Tenancy` (`Tenant`, `TenantDomain`, lifecycle state machine) |
| `FeatureEntitlement` | `modules/FeatureManagement` (flags become entitlements) |
| `AuditLog` | the per-module `*AuditRecorder` pattern |

Nothing in the existing codebase corresponds to the Feature **registry**,
**packaging**, **installation**, or **job** concepts — those are entirely new
(see [`current-state-analysis.md`](current-state-analysis.md) §7).
