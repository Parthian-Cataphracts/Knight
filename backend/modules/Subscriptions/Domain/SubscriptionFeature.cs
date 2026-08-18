using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Subscriptions.Domain;

/// <summary>
/// An optional feature the customer has chosen on their subscription.
///
/// Disabling keeps the row rather than deleting it: who turned a paid capability
/// on, and when, is billing evidence, and it is also the difference between "was
/// never selected" and "was selected and then dropped".
/// </summary>
public sealed class SubscriptionFeature : Entity
{
    public Guid SubscriptionId { get; private set; }

    public Guid FeatureId { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTimeOffset EnabledAt { get; private set; }

    /// <summary>The account that made the choice; null when automation did.</summary>
    public Guid? EnabledBy { get; private set; }

    public DateTimeOffset? DisabledAt { get; private set; }

    private SubscriptionFeature()
    {
    }

    private SubscriptionFeature(Guid id, Guid subscriptionId, Guid featureId, Guid? enabledBy, DateTimeOffset enabledAt)
        : base(id)
    {
        SubscriptionId = subscriptionId;
        FeatureId = featureId;
        EnabledBy = enabledBy;
        EnabledAt = enabledAt;
        IsEnabled = true;
    }

    internal static SubscriptionFeature Create(Guid subscriptionId, Guid featureId, Guid? enabledBy, DateTimeOffset enabledAt)
    {
        if (featureId == Guid.Empty)
        {
            throw DomainException.Validation("A subscription feature must name a feature.");
        }

        return new SubscriptionFeature(Guid.NewGuid(), subscriptionId, featureId, enabledBy, enabledAt);
    }

    internal void Enable(Guid? enabledBy, DateTimeOffset now)
    {
        IsEnabled = true;
        EnabledBy = enabledBy;
        EnabledAt = now;
        DisabledAt = null;
    }

    internal void Disable(DateTimeOffset now)
    {
        if (!IsEnabled)
        {
            throw DomainException.Conflict("The feature is already disabled on this subscription.");
        }

        IsEnabled = false;
        DisabledAt = now;
    }
}
