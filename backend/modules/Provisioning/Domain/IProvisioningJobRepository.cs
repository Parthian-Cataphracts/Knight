namespace Provisioning.Domain;

/// <summary>
/// Persistence for provisioning jobs. Customer-scoped like everything else a
/// customer can see: a customer watching their own store come up must not be
/// able to read anybody else's provisioning run.
/// </summary>
public interface IProvisioningJobRepository
{
    Task<ProvisioningJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns the job a repeated request already created, rather than starting a second run against the same store.</summary>
    Task<ProvisioningJob?> FindByIdempotencyKeyAsync(Guid storeId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The unfinished job for a store, if there is one. Only one provisioning run per store at a time.</summary>
    Task<ProvisioningJob?> FindActiveForStoreAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>
    /// Jobs the coordinator should look at: running, or waiting on something
    /// that may since have happened. A job awaiting an operator is included —
    /// the fact it waits for may arrive on its own, as a domain verification
    /// does.
    /// </summary>
    Task<IReadOnlyCollection<ProvisioningJob>> ListAdvanceableAsync(int limit, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<ProvisioningJob> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        ProvisioningState? state,
        CancellationToken cancellationToken);

    Task AddAsync(ProvisioningJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a step the aggregate has just created. Required for the same
    /// reason the delivery repository needs it: a child of a loaded aggregate
    /// whose key came from the domain is not reliably classified as an insert.
    /// </summary>
    void RegisterNewStep(ProvisioningStepResult step);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
