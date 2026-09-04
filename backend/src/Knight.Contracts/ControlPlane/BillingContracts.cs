namespace Knight.Contracts.ControlPlane;

/// <summary>
/// The public price list and self-service checkout (docs/self-service-saas-plan.md §6).
/// These are the shapes an anonymous visitor and a signed-in customer owner see;
/// the operations dashboard has its own, richer, contracts.
/// </summary>
public sealed record PublicFeatureResponse
{
    public required Guid FeatureId { get; init; }

    public required string Slug { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }
}

public sealed record PublicOptionalFeatureResponse
{
    public required Guid FeatureId { get; init; }

    public required string Slug { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Null when no price is in force — the add-on is offered but not yet priced, and cannot be bought.</summary>
    public decimal? Price { get; init; }

    public required string Currency { get; init; }
}

public sealed record PublicPlanResponse
{
    public required Guid Id { get; init; }

    public required string Key { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required decimal BasePrice { get; init; }

    public required string Currency { get; init; }

    public required IReadOnlyCollection<PublicFeatureResponse> IncludedFeatures { get; init; }

    public required IReadOnlyCollection<PublicOptionalFeatureResponse> OptionalFeatures { get; init; }
}

public sealed record CheckoutRequestBody
{
    public required Guid PlanId { get; init; }

    /// <summary>"monthly" (default) or "yearly".</summary>
    public string? BillingInterval { get; init; }

    public IReadOnlyCollection<Guid>? SelectedFeatureIds { get; init; }

    /// <summary>The payment provider to use; the default is taken from configuration when omitted.</summary>
    public string? Provider { get; init; }
}

public sealed record CheckoutResponse
{
    public required Guid CheckoutSessionId { get; init; }

    public required Guid SubscriptionId { get; init; }

    public required string CheckoutUrl { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}
