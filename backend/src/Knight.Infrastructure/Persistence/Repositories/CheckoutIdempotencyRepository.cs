using System.Data;
using Checkout.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Knight.Application.Exceptions;

namespace Knight.Infrastructure.Persistence.Repositories;

public sealed class CheckoutIdempotencyRepository : ICheckoutIdempotencyRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly PlatformDbContext _context;

    public CheckoutIdempotencyRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<CheckoutIdempotencyRecord?> GetByKeyHashAsync(Guid tenantId, string keyHash, CancellationToken cancellationToken)
    {
        return await _context.CheckoutIdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.KeyHash == keyHash, cancellationToken);
    }

    public async Task AddAsync(CheckoutIdempotencyRecord record, CancellationToken cancellationToken)
    {
        await _context.CheckoutIdempotencyRecords.AddAsync(record, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                var result = await operation();
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                throw new IdempotencyKeyClaimConflictException();
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                throw;
            }
        });
    }

    // Classified by PostgreSQL SQLSTATE only, never by matching exception text —
    // consistent with every other repository here (see CategoryRepository).
    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
