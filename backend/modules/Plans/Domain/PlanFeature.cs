using Knight.Domain.Exceptions;

namespace Plans.Domain;

/// <summary>
/// One feature's place in a plan.
///
/// Three states are representable and all three are meaningful:
/// included and not toggleable (part of the product), not included but
/// toggleable (an upsell the customer may switch on), and included and
/// toggleable (bundled, but the customer may opt out).
/// </summary>
public sealed class PlanFeature
{
    public Guid PlanId { get; private set; }

    public Guid FeatureId { get; private set; }

    /// <summary>True when the plan grants the feature without the customer choosing it.</summary>
    public bool IsIncluded { get; private set; }

    /// <summary>
    /// Whether the customer may change this themselves. A customer switching a
    /// non-toggleable feature is refused — the plan decides, not the customer
    /// (docs/domain-model.md section 4).
    /// </summary>
    public bool IsCustomerToggleable { get; private set; }

    /// <summary>
    /// Optional semver range pinning which versions of the feature this plan
    /// accepts. Null means the latest compatible published version. Interpreted
    /// by the delivery engine in phase 3.5; stored here because the constraint
    /// is a commercial decision, not a technical one.
    /// </summary>
    public string? PinnedVersionRange { get; private set; }

    private PlanFeature()
    {
    }

    private PlanFeature(Guid planId, Guid featureId, bool isIncluded, bool isCustomerToggleable, string? pinnedVersionRange)
    {
        PlanId = planId;
        FeatureId = featureId;
        IsIncluded = isIncluded;
        IsCustomerToggleable = isCustomerToggleable;
        PinnedVersionRange = pinnedVersionRange;
    }

    internal static PlanFeature Create(
        Guid planId,
        Guid featureId,
        bool isIncluded,
        bool isCustomerToggleable,
        string? pinnedVersionRange)
        => new(planId, featureId, isIncluded, isCustomerToggleable, NormalizeRange(pinnedVersionRange));

    internal void Update(bool isIncluded, bool isCustomerToggleable, string? pinnedVersionRange)
    {
        IsIncluded = isIncluded;
        IsCustomerToggleable = isCustomerToggleable;
        PinnedVersionRange = NormalizeRange(pinnedVersionRange);
    }

    private static string? NormalizeRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return null;
        }

        var trimmed = range.Trim();
        if (trimmed.Length > 100)
        {
            throw DomainException.Validation("A pinned version range must be 100 characters or fewer.");
        }

        return trimmed;
    }
}
