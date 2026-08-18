namespace Plans.Domain;

/// <summary>
/// Persistence for plans. Like features, plans are platform-owned: the price
/// list is the same for everyone, so nothing here is customer filtered.
/// </summary>
public interface IPlanRepository
{
    Task<Plan?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Plan?> GetByKeyAsync(string key, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Plan>> ListAsync(bool includeInactive, CancellationToken cancellationToken);

    Task AddAsync(Plan plan, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a plan-feature row created through the aggregate as an insert.
    /// EF Core cannot reliably classify a new child discovered only by walking a
    /// tracked parent's navigation.
    /// </summary>
    void RegisterNewFeature(PlanFeature feature);

    void RemoveFeature(PlanFeature feature);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IFeaturePriceRepository
{
    /// <summary>Every price in force at the given moment, for the given features.</summary>
    Task<IReadOnlyCollection<FeaturePrice>> GetApplicableAsync(
        IReadOnlyCollection<Guid> featureIds,
        Guid planId,
        DateTimeOffset moment,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FeaturePrice>> ListForFeatureAsync(Guid featureId, CancellationToken cancellationToken);

    Task AddAsync(FeaturePrice price, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
