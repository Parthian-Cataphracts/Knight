using Microsoft.EntityFrameworkCore;
using Provisioning.Domain;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Persistence for provisioning jobs. Customer-owned, so the context's global
/// isolation filter has already narrowed every query here; repeating the
/// condition would only create somewhere for the two to disagree.
/// </summary>
internal sealed class ProvisioningJobRepository : IProvisioningJobRepository
{
    private readonly ControlPlaneDbContext _context;

    public ProvisioningJobRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<ProvisioningJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.ProvisioningJobs
            .Include(job => job.Steps)
            .FirstOrDefaultAsync(job => job.Id == id, cancellationToken);

    public Task<ProvisioningJob?> FindByIdempotencyKeyAsync(
        Guid storeId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        _context.ProvisioningJobs
            .Include(job => job.Steps)
            .FirstOrDefaultAsync(
                job => job.StoreId == storeId && job.IdempotencyKey == idempotencyKey,
                cancellationToken);

    public Task<ProvisioningJob?> FindActiveForStoreAsync(Guid storeId, CancellationToken cancellationToken) =>
        _context.ProvisioningJobs
            .Include(job => job.Steps)
            .FirstOrDefaultAsync(
                job => job.StoreId == storeId &&
                       (job.State == ProvisioningState.Running || job.State == ProvisioningState.AwaitingOperator),
                cancellationToken);

    /// <summary>
    /// Everything unfinished, oldest touched first so a backlog drains rather
    /// than starving the store that has been waiting longest. Failed jobs are
    /// excluded: they need a person to retry them, and re-evaluating one every
    /// minute would only rewrite the same failure.
    /// </summary>
    public async Task<IReadOnlyCollection<ProvisioningJob>> ListAdvanceableAsync(int limit, CancellationToken cancellationToken) =>
        await _context.ProvisioningJobs
            .Include(job => job.Steps)
            .Where(job => job.State == ProvisioningState.Running || job.State == ProvisioningState.AwaitingOperator)
            .OrderBy(job => job.UpdatedAt ?? job.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyCollection<ProvisioningJob> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        ProvisioningState? state,
        CancellationToken cancellationToken)
    {
        var query = _context.ProvisioningJobs.Include(job => job.Steps).AsQueryable();

        if (storeId is { } store)
        {
            query = query.Where(job => job.StoreId == store);
        }

        if (customerId is { } customer)
        {
            query = query.Where(job => job.CustomerId == customer);
        }

        if (state is { } wanted)
        {
            query = query.Where(job => job.State == wanted);
        }

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(job => job.CreatedAt)
            .ThenBy(job => job.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task AddAsync(ProvisioningJob job, CancellationToken cancellationToken) =>
        await _context.ProvisioningJobs.AddAsync(job, cancellationToken);

    public void RegisterNewStep(ProvisioningStepResult step) =>
        _context.Entry(step).State = EntityState.Added;

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}
