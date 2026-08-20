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

/// <summary>
/// Persistence for staged rollouts.
///
/// Deliberately *not* customer-scoped. A rollout spans every store entitled to a
/// Feature, across customers, and is a platform operation — which is also why the
/// endpoints that reach it are platform-only.
/// </summary>
public interface IFeatureRolloutRepository
{
    Task<FeatureRollout?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// The live rollout of a Feature, if there is one. Two concurrent rollouts of
    /// the same Feature would race each other onto the same stores, so planning
    /// one checks here first.
    /// </summary>
    Task<FeatureRollout?> FindActiveForFeatureAsync(Guid featureId, CancellationToken cancellationToken);

    /// <summary>The rollout a given installation job belongs to, if any — the result-reporting path.</summary>
    Task<FeatureRollout?> FindByJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Rollouts with waves still to dispatch, for the coordinator sweep.</summary>
    Task<IReadOnlyCollection<FeatureRollout>> ListAdvanceableAsync(CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<FeatureRollout> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        RolloutState? state,
        CancellationToken cancellationToken);

    Task AddAsync(FeatureRollout rollout, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
