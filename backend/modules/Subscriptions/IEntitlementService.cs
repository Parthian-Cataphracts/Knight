using Subscriptions.Domain;

namespace Subscriptions;

public sealed record EntitlementView(
    Guid FeatureId,
    string Source,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt,
    bool IsActive);

/// <summary>
/// Why a feature could not be entitled. Returned rather than thrown for the
/// checks a caller may legitimately want to ask about before acting — the
/// install-preview dialog in the dashboard shows exactly these reasons.
/// </summary>
public enum EntitlementRefusal
{
    None = 0,

    /// <summary>The feature is a draft or withdrawn, so nobody may hold it yet or any longer.</summary>
    FeatureNotAvailable = 1,

    /// <summary>The plan does not offer it, or offers it but not for the customer to choose.</summary>
    NotOfferedByPlan = 2,

    /// <summary>The customer has no subscription that entitles anything.</summary>
    NoEntitlingSubscription = 3,

    /// <summary>The feature needs a machine the customer does not share, and they only have shared hosting.</summary>
    RequiresDedicatedInfrastructure = 4,
}

public sealed record EntitlementDecision(EntitlementRefusal Refusal, string? Detail = null)
{
    public bool IsAllowed => Refusal is EntitlementRefusal.None;

    public static EntitlementDecision Allowed { get; } = new(EntitlementRefusal.None);
}

/// <summary>
/// The single source of truth for what a customer is owed
/// (docs/domain-model.md section 4). Nothing else in the platform may decide
/// entitlement, and no client-sent value is ever trusted for it.
/// </summary>
public interface IEntitlementService
{
    /// <summary>Every entitlement the customer holds, active ones by default.</summary>
    Task<IReadOnlyCollection<EntitlementView>> ResolveForCustomerAsync(
        Guid customerId,
        bool includeInactive,
        CancellationToken cancellationToken);

    /// <summary>Whether one specific feature is currently entitled to the customer.</summary>
    Task<bool> IsEntitledAsync(Guid customerId, Guid featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the feature could be entitled to this customer, and if not, why.
    /// Side-effect free: this is the question the dashboard asks before offering
    /// a button.
    /// </summary>
    Task<EntitlementDecision> CanEntitleAsync(Guid customerId, Guid featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Grants a feature by hand, outside any plan. Used for pilots and goodwill;
    /// the granting account is recorded on the entitlement itself.
    /// </summary>
    Task<EntitlementView> GrantAsync(
        Guid customerId,
        Guid featureId,
        Guid grantedBy,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken);

    Task RevokeAsync(Guid customerId, Guid featureId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Brings the customer's entitlements into line with their subscription:
    /// grants what the plan includes and what they have chosen, revokes what they
    /// no longer hold. Manual grants are left alone — they were made
    /// deliberately, outside the plan, and a plan change is not a decision to
    /// withdraw them.
    /// </summary>
    Task ReconcileAsync(Guid customerId, CancellationToken cancellationToken);
}
