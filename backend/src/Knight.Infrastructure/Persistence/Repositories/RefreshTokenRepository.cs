using Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly PlatformDbContext _context;

    public RefreshTokenRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        _context.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public async Task AddAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        await _context.RefreshTokens.AddAsync(token, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<RefreshTokenConsumeOutcome> TryConsumeAndIssueAsync(Guid oldTokenId, RefreshToken newToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        // A single conditional UPDATE is the atomicity boundary: PostgreSQL's row
        // lock ensures that if two requests race to consume the same token, only
        // one UPDATE can see ConsumedAt/RevokedAt as still null and affect a row —
        // the other necessarily affects zero rows. See
        // docs/architecture/authorization.md ("atomic rotation").
        var affected = await _context.RefreshTokens
            .Where(t => t.Id == oldTokenId && t.ConsumedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.ConsumedAt, now)
                .SetProperty(t => t.ReplacedByTokenId, newToken.Id), cancellationToken);

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);

            var exists = await _context.RefreshTokens.AsNoTracking().AnyAsync(t => t.Id == oldTokenId, cancellationToken);
            return exists ? RefreshTokenConsumeOutcome.AlreadyConsumedOrRevoked : RefreshTokenConsumeOutcome.NotFound;
        }

        await _context.RefreshTokens.AddAsync(newToken, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RefreshTokenConsumeOutcome.Consumed;
    }

    public Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        _context.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.RevokedReason, reason), cancellationToken);

    public Task<int> RevokeAllActiveForSubjectAsync(SubjectType subjectType, Guid subjectId, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        _context.RefreshTokens
            .Where(t => t.SubjectType == subjectType && t.SubjectId == subjectId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.RevokedReason, reason), cancellationToken);
}
