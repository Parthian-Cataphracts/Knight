using Provisioning.Domain;

namespace Provisioning;

public sealed record ProvisioningJobPage(
    IReadOnlyCollection<ProvisioningJob> Items,
    int Page,
    int PageSize,
    long TotalCount);

public sealed record ProvisioningJobQuery(int Page, int PageSize, Guid? StoreId, Guid? CustomerId, ProvisioningState? State);

/// <summary>
/// Drives a store from registered to Active, and from Active to purged.
///
/// The service never talks to a store or a machine itself. It reads the facts
/// other modules already record — a credential exists, an agent enrolled, the
/// store handshaked, its Features installed, it reported healthy — and moves the
/// job on when they are true. That is what makes provisioning resumable and
/// honest: a step is finished because something happened, not because a request
/// returned 200.
/// </summary>
public interface IProvisioningService
{
    /// <summary>
    /// Starts (or returns) the provisioning run for a store. Repeating the
    /// request with the same idempotency key returns the same job.
    /// </summary>
    Task<ProvisioningJob> StartProvisioningAsync(Guid storeId, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the deprovisioning run: disable, revoke, stop, retain, export,
    /// purge. The retention window is resolved once, here, from the customer's
    /// override or their plan.
    /// </summary>
    Task<ProvisioningJob> StartDeprovisioningAsync(Guid storeId, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Evaluates the job's current step and moves it as far as the facts allow.
    /// Safe to call repeatedly — it is what the coordinator does on a timer.
    /// </summary>
    Task<ProvisioningJob> AdvanceAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Records that a person did what only a person can do, and then advances.</summary>
    Task<ProvisioningJob> CompleteManualStepAsync(Guid jobId, string stepName, string? detail, CancellationToken cancellationToken);

    /// <summary>Clears the failed step and resumes from it. Succeeded steps are not re-run.</summary>
    Task<ProvisioningJob> RetryAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ProvisioningJob> CancelAsync(Guid jobId, string reason, CancellationToken cancellationToken);

    Task<ProvisioningJob?> GetAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ProvisioningJobPage> ListAsync(ProvisioningJobQuery query, CancellationToken cancellationToken);

    /// <summary>Advances every job that may have moved on. The coordinator's whole job.</summary>
    Task<int> AdvanceDueAsync(int limit, CancellationToken cancellationToken);
}
