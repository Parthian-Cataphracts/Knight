using AutoAdmin.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Persistence for the Automatic Admin. Both roots are customer-owned, so the
/// context's isolation filter already narrows every query to the current
/// customer; the explicit customer argument on the list query is the query's own
/// meaning, not the isolation.
/// </summary>
internal sealed class AutoAdminSettingsRepository : IAutoAdminSettingsRepository
{
    private readonly ControlPlaneDbContext _context;

    public AutoAdminSettingsRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<AutoAdminSettings?> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        _context.AutoAdminSettings.FirstOrDefaultAsync(s => s.CustomerId == customerId, cancellationToken);

    public async Task AddAsync(AutoAdminSettings settings, CancellationToken cancellationToken) =>
        await _context.AutoAdminSettings.AddAsync(settings, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);
}

internal sealed class ContentJobRepository : IContentJobRepository
{
    private readonly ControlPlaneDbContext _context;

    public ContentJobRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<ContentJob?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _context.ContentJobs
            .Include(job => job.Drafts)
            .Include(job => job.Publications)
            .FirstOrDefaultAsync(job => job.Id == id, cancellationToken);

    public async Task AddAsync(ContentJob job, CancellationToken cancellationToken) =>
        await _context.ContentJobs.AddAsync(job, cancellationToken);

    public async Task<IReadOnlyCollection<ContentJob>> ListForCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        await _context.ContentJobs
            .Include(job => job.Drafts)
            .Include(job => job.Publications)
            .Where(job => job.CustomerId == customerId)
            .OrderByDescending(job => job.CreatedAt)
            .ToArrayAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        // A publication (or a draft) is immutable once recorded. When approval
        // adds one to a job that was loaded rather than created here, EF reads its
        // client-assigned key as an existing row and marks it Modified, so the
        // update finds nothing to change. It is a new row: make it an insert.
        foreach (var entry in _context.ChangeTracker.Entries<Publication>())
        {
            PromoteToInsert(entry);
        }

        foreach (var entry in _context.ChangeTracker.Entries<ContentDraft>())
        {
            PromoteToInsert(entry);
        }

        return _context.SaveChangesAsync(cancellationToken);
    }

    private static void PromoteToInsert(EntityEntry entry)
    {
        if (entry.State is EntityState.Modified)
        {
            entry.State = EntityState.Added;
        }
    }
}
