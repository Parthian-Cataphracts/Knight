namespace FeatureDelivery.Domain;

/// <summary>
/// Persistence for installations. Customer-scoped: the isolation filter means a
/// customer-scoped principal sees their own stores' installations and nobody
/// else's, without every query remembering to say so.
/// </summary>
public interface IFeatureInstallationRepository
{
    Task<FeatureInstallation?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<FeatureInstallation?> FindAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FeatureInstallation>> ListForStoreAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>Every installation of one feature across a customer's stores — what an entitlement change acts on.</summary>
    Task<IReadOnlyCollection<FeatureInstallation>> ListForCustomerFeatureAsync(
        Guid customerId,
        Guid featureId,
        CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<FeatureInstallation> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        InstallationState? state,
        CancellationToken cancellationToken);

    /// <summary>Uninstalled features whose retention window has closed — the purge sweep's input.</summary>
    Task<IReadOnlyCollection<FeatureInstallation>> ListPurgeableAsync(DateTimeOffset asOf, CancellationToken cancellationToken);

    Task AddAsync(FeatureInstallation installation, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IFeatureInstallationJobRepository
{
    Task<FeatureInstallationJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds the job a request already created, so a retried request returns it instead of queuing a second.</summary>
    Task<FeatureInstallationJob?> FindByIdempotencyKeyAsync(
        Guid storeId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// The job a store's agent should run next, or null when there is none.
    ///
    /// Only one job runs per store at a time, so this returns nothing while one
    /// is already claimed: two agents installing into one Django project at once
    /// is a corrupted virtual environment, not twice the throughput.
    /// </summary>
    Task<FeatureInstallationJob?> FindNextForStoreAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>True when the store already has a job claimed or queued ahead of this one.</summary>
    Task<bool> HasUnfinishedJobAsync(Guid storeId, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<FeatureInstallationJob> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        JobState? state,
        CancellationToken cancellationToken);

    /// <summary>Running jobs whose claim has lapsed — what the timeout sweep returns to the queue or fails.</summary>
    Task<IReadOnlyCollection<FeatureInstallationJob>> ListExpiredClaimsAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken);

    Task AddAsync(FeatureInstallationJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a step the aggregate has just created.
    ///
    /// A child added to an already-loaded aggregate is not reliably picked up as
    /// an insert: the key is assigned in the domain rather than by the database,
    /// so the change tracker can take the new row for an existing one and issue
    /// an update that matches nothing. Saying so explicitly is the same fix the
    /// plan and subscription repositories use for their own child collections.
    /// </summary>
    void RegisterNewStep(JobStepResult step);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IFeatureConfigurationRepository
{
    Task<FeatureConfiguration?> FindAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<FeatureConfiguration>> ListForStoreAsync(Guid storeId, CancellationToken cancellationToken);

    Task AddAsync(FeatureConfiguration configuration, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
