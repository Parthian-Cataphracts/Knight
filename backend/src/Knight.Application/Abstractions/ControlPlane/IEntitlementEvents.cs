namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// A customer became owed a capability. Phase 3.5's delivery engine consumes
/// this to queue an installation: the grant is the commercial decision, and
/// delivery is the consequence, never the same act
/// (docs/feature-delivery.md §2).
/// </summary>
public sealed record FeatureEntitlementGranted(
    Guid CustomerId,
    Guid FeatureId,
    string Source,
    DateTimeOffset OccurredAt);

/// <summary>
/// A customer stopped being owed a capability. The consequence is to
/// **disable** the installed feature, not to uninstall it and not to delete its
/// data — a customer who resubscribes must find their data where they left it
/// (docs/adr/0016-feature-migration-and-removal-policy.md).
/// </summary>
public sealed record FeatureEntitlementRevoked(
    Guid CustomerId,
    Guid FeatureId,
    string Reason,
    DateTimeOffset OccurredAt);

/// <summary>
/// Publishes entitlement changes to whatever acts on them. Declared in the
/// application layer so the module that owns entitlements does not have to know
/// that a delivery engine exists — in phase 2 nothing consumes these beyond the
/// audit trail, and that is deliberate: the commercial half must be correct and
/// observable before code starts moving on the strength of it.
/// </summary>
public interface IEntitlementEventPublisher
{
    Task PublishAsync(FeatureEntitlementGranted @event, CancellationToken cancellationToken);

    Task PublishAsync(FeatureEntitlementRevoked @event, CancellationToken cancellationToken);
}

/// <summary>
/// Answers how a customer's stores are hosted, for the one entitlement rule that
/// depends on it: a feature needing dedicated infrastructure cannot be entitled
/// to a customer running only on shared hosting.
///
/// A port rather than a module reference — the module that owns entitlements and
/// the module that owns stores stay independent of each other.
/// </summary>
public interface IStoreHostingReader
{
    /// <summary>
    /// True when the customer has at least one store that is not on shared
    /// hosting. False for a customer with no stores at all: nothing to run a
    /// dedicated-infrastructure capability on is not the same as being ready for
    /// one.
    /// </summary>
    Task<bool> HasDedicatedCapacityAsync(Guid customerId, CancellationToken cancellationToken);
}
