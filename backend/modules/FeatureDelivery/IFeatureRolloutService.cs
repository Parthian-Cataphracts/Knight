using FeatureDelivery.Domain;

namespace FeatureDelivery;

/// <summary>
/// Asks for a rollout of one Feature version.
///
/// <paramref name="WavePercentages"/> describes the waves *after* the canary,
/// which is always one store. Empty means one wave with everything else in it,
/// which is a legitimate choice for a small fleet and a poor one for a large.
/// </summary>
public sealed record PlanRolloutInput(
    string Slug,
    string Version,
    IReadOnlyList<int> WavePercentages,
    int FailureThreshold,

    /// <summary>
    /// Restricts the rollout to these stores. Null means every store that is
    /// entitled to the Feature and not already on the target version.
    /// </summary>
    IReadOnlyList<Guid>? StoreIds = null,

    /// <summary>
    /// Forces a particular store to be the canary. Without it the service picks
    /// the safest available: a non-production store if the fleet has one.
    /// </summary>
    Guid? CanaryStoreId = null);

public sealed record RolloutPage(
    IReadOnlyCollection<FeatureRollout> Items,
    int Page,
    int PageSize,
    long TotalCount);

/// <summary>
/// Rolls one Feature version out across many stores, a wave at a time.
///
/// This is the mitigation R16 in [`risks.md`](../../../docs/risks.md) calls for.
/// KNIGHT delivers executable code into customer production systems, so the
/// dangerous operation is not installing a Feature into one store — that is
/// reviewed and reversible — it is installing a new version everywhere at once.
///
/// The service owns *sequencing* only. Each store's actual work is an ordinary
/// installation job, queued through <see cref="IFeatureDeliveryService"/> and
/// carried out by that store's agent
/// ([`adr/0015`](../../../docs/adr/0015-feature-delivery-mechanism.md)). Nothing
/// here can make an agent do anything the job vocabulary does not already allow.
/// </summary>
public interface IFeatureRolloutService
{
    /// <summary>
    /// Plans a rollout and returns it without starting it, so an operator can see
    /// which stores are in which wave before anything is queued.
    /// </summary>
    Task<FeatureRollout> PlanAsync(PlanRolloutInput input, CancellationToken cancellationToken);

    /// <summary>Starts a planned rollout and dispatches its canary wave.</summary>
    Task<FeatureRollout> StartAsync(Guid rolloutId, CancellationToken cancellationToken);

    /// <summary>
    /// Dispatches the next wave if the rollout is ready for one.
    ///
    /// Called by the coordinator sweep rather than by a request. A wave becomes
    /// ready when the wave before it has reported on every store, which happens
    /// when an agent finishes — at a moment no HTTP request is waiting on.
    /// </summary>
    Task<FeatureRollout?> AdvanceAsync(Guid rolloutId, CancellationToken cancellationToken);

    /// <summary>Advances every rollout that is ready, and answers how many waves went out.</summary>
    Task<int> AdvanceAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records what an installation job did, against the rollout it belongs to.
    ///
    /// Does nothing when the job is not part of a rollout, which is the common
    /// case: most installs are a single store asked for by hand.
    /// </summary>
    Task RecordJobOutcomeAsync(Guid jobId, bool succeeded, string? detail, CancellationToken cancellationToken);

    Task<FeatureRollout> HaltAsync(Guid rolloutId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a halted rollout. The failures that halted it are kept and
    /// accepted rather than cleared, so the next failure halts it again.
    /// </summary>
    Task<FeatureRollout> ResumeAsync(Guid rolloutId, CancellationToken cancellationToken);

    /// <summary>
    /// Stops a rollout for good. Stores already upgraded stay upgraded: a rollout
    /// is not a transaction, and automatically downgrading working production
    /// stores would be a worse outage than the one being avoided.
    /// </summary>
    Task<FeatureRollout> CancelAsync(Guid rolloutId, string reason, CancellationToken cancellationToken);

    Task<FeatureRollout?> GetAsync(Guid rolloutId, CancellationToken cancellationToken);

    Task<RolloutPage> ListAsync(int page, int pageSize, RolloutState? state, CancellationToken cancellationToken);
}
