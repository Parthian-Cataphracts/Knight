namespace Knight.Contracts.ControlPlane;

/// <summary>
/// The customer self-service surface (docs/self-service-saas-plan.md §6, /me).
/// Everything here is scoped to the authenticated customer by the principal, never
/// by a client-supplied id, and is deliberately a friendlier, smaller projection
/// than the operations dashboard's own contracts.
/// </summary>
public sealed record MeSubscriptionResponse
{
    public required Guid Id { get; init; }

    public required Guid PlanId { get; init; }

    public required string PlanName { get; init; }

    /// <summary>trial | active | past_due | suspended | cancelled | pending.</summary>
    public required string Status { get; init; }

    public required DateTimeOffset CurrentPeriodEnd { get; init; }

    public required bool CancelAtPeriodEnd { get; init; }

    public required IReadOnlyCollection<Guid> FeatureIds { get; init; }
}

public sealed record MeStoreResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required string PrimaryDomain { get; init; }

    /// <summary>The store lifecycle: pending | active | suspended | archived.</summary>
    public required string Status { get; init; }

    public required string IntegrationStatus { get; init; }

    /// <summary>True once the store is Active and reachable — the customer can open it.</summary>
    public required bool IsReady { get; init; }
}

public sealed record MeProvisioningStepResponse
{
    public required string Name { get; init; }

    public required string Status { get; init; }
}

public sealed record MeProvisioningResponse
{
    public required Guid StoreId { get; init; }

    /// <summary>provisioning | ready | failed | awaiting_operator | none.</summary>
    public required string State { get; init; }

    /// <summary>A short, customer-friendly line about what is happening now.</summary>
    public required string FriendlyStatus { get; init; }

    public required int PercentComplete { get; init; }

    public required IReadOnlyCollection<MeProvisioningStepResponse> Steps { get; init; }
}
