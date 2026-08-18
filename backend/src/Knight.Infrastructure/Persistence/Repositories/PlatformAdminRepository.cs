using Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class PlatformAdminRepository : IPlatformAdminRepository
{
    private readonly PlatformDbContext _context;

    public PlatformAdminRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<PlatformAdmin?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.PlatformAdmins.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<PlatformAdmin?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        _context.PlatformAdmins.FirstOrDefaultAsync(a => a.NormalizedEmail == normalizedEmail, cancellationToken);

    public async Task AddAsync(PlatformAdmin admin, CancellationToken cancellationToken)
    {
        await _context.PlatformAdmins.AddAsync(admin, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}
