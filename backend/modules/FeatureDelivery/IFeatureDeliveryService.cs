using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;

namespace FeatureDelivery;

public sealed record InstallFeatureInput(
    Guid StoreId,
    string Slug,
    string? VersionRange,
    string? IdempotencyKey,
    JobTrigger Trigger = JobTrigger.Manual);

public sealed record InstallationPage(
    IReadOnlyCollection<FeatureInstallation> Items,
    int Page,
    int PageSize,
    long TotalCount);

public sealed record JobPage(
    IReadOnlyCollection<FeatureInstallationJob> Items,
    int Page,
    int PageSize,
    long TotalCount);

/// <summary>
/// What a caller gets back from asking for an install: either the job that was
/// queued, or the plan that explains why no job was.
///
/// Both are returned rather than one being an error, because "we could not
/// install this and here is exactly which constraint failed" is a successful
/// answer to the question. The customer may still be entitled; the installation
/// simply stays <see cref="InstallationState.NotInstalled"/> with a blocking
/// reason recorded (docs/feature-delivery.md §8).
/// </summary>
public sealed record InstallationRequestResult(
    FeaturePlan Plan,
    IReadOnlyList<FeatureInstallationJob> QueuedJobs,
    FeatureInstallation Installation)
{
    public bool WasQueued => QueuedJobs.Count > 0;
}

/// <summary>
/// Turns decisions about features into work a store's agent will carry out.
///
/// The service never touches a store itself. It resolves, records and queues;
/// the agent polls, does the work, and reports back. That separation is what
/// makes the whole model survive a store being unreachable: an install requested
/// while a store is down is a queued job, not a failed request.
/// </summary>
public interface IFeatureDeliveryService
{
    /// <summary>
    /// Resolves what installing a Feature would do, without changing anything.
    /// This is what the dashboard's install preview shows before an operator
    /// confirms an irreversible migration.
    /// </summary>
    Task<FeaturePlan> PreviewAsync(Guid storeId, string slug, string? versionRange, CancellationToken cancellationToken);

    Task<InstallationRequestResult> InstallAsync(InstallFeatureInput input, CancellationToken cancellationToken);

    /// <summary>Moves an installed Feature to a newer version. The same pipeline, plus a compatibility check of the new version.</summary>
    Task<InstallationRequestResult> UpgradeAsync(InstallFeatureInput input, CancellationToken cancellationToken);

    /// <summary>Switches a Feature off, leaving its code and data in place.</summary>
    Task<FeatureInstallationJob> DisableAsync(Guid storeId, Guid featureId, string reason, CancellationToken cancellationToken);

    Task<FeatureInstallationJob> EnableAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a Feature's code. The data is kept for the manifest's retention
    /// window, so this is deliberate and audited rather than a consequence of
    /// anything happening automatically.
    /// </summary>
    Task<FeatureInstallationJob> UninstallAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken);

    /// <summary>Returns a store to the version it was running before a failed upgrade.</summary>
    Task<FeatureInstallationJob> RollbackAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a Feature's configuration and queues the cheap job that applies
    /// it. Secret values are encrypted before they are stored and are never
    /// returned by any read path.
    /// </summary>
    Task<FeatureInstallationJob> ConfigureAsync(
        Guid storeId,
        Guid featureId,
        string valuesJson,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken);

    Task<InstallationPage> ListInstallationsAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        InstallationState? state,
        CancellationToken cancellationToken);

    Task<FeatureInstallation?> GetInstallationAsync(Guid id, CancellationToken cancellationToken);

    Task<JobPage> ListJobsAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        JobState? state,
        CancellationToken cancellationToken);

    Task<FeatureInstallationJob?> GetJobAsync(Guid id, CancellationToken cancellationToken);

    Task<FeatureInstallationJob> CancelJobAsync(Guid jobId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Applies an entitlement change. Granting resolves and queues an install
    /// across the customer's stores; losing one disables what is installed and
    /// never uninstalls it (docs/feature-delivery.md §11).
    /// </summary>
    Task ApplyEntitlementChangeAsync(Guid customerId, Guid featureId, bool entitled, string reason, CancellationToken cancellationToken);
}
