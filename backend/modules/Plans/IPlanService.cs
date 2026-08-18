using Knight.Domain.Common;
using Plans.Domain;

namespace Plans;

public sealed record CreatePlanInput(string Key, string Name, string? Description, decimal BasePrice, string Currency, int SortOrder);

public sealed record UpdatePlanInput(string Name, string? Description, decimal BasePrice, string Currency, int SortOrder);

public sealed record SetPlanFeatureInput(Guid FeatureId, bool IsIncluded, bool IsCustomerToggleable, string? PinnedVersionRange);

public sealed record SetFeaturePriceInput(Guid FeatureId, Guid? PlanId, decimal Amount, string Currency, BillingPeriod BillingPeriod);

/// <summary>
/// Plan and price administration. Plans are data: everything here writes rows,
/// and nothing anywhere else in the platform decides what a plan contains or what
/// it costs.
/// </summary>
public interface IPlanService
{
    Task<IReadOnlyCollection<Plan>> ListAsync(bool includeInactive, CancellationToken cancellationToken);

    Task<Plan?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Plan> CreateAsync(CreatePlanInput input, CancellationToken cancellationToken);

    Task<Plan> UpdateAsync(Guid id, UpdatePlanInput input, CancellationToken cancellationToken);

    Task<Plan> ActivateAsync(Guid id, CancellationToken cancellationToken);

    Task<Plan> DeactivateAsync(Guid id, CancellationToken cancellationToken);

    Task<Plan> SetFeatureAsync(Guid id, SetPlanFeatureInput input, CancellationToken cancellationToken);

    Task<Plan> RemoveFeatureAsync(Guid id, Guid featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a new price for a feature and closes whatever it replaces, so past
    /// invoices stay explicable from the prices that were in force when they were
    /// issued.
    /// </summary>
    Task<FeaturePrice> SetPriceAsync(SetFeaturePriceInput input, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FeaturePrice>> ListPricesAsync(Guid featureId, CancellationToken cancellationToken);
}
