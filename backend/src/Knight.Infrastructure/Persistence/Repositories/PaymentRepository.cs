using Microsoft.EntityFrameworkCore;
using Payment.Domain;
using Knight.Application.Exceptions;
using Knight.Infrastructure.Persistence;

namespace Knight.Infrastructure.Persistence.Repositories;

public sealed class PaymentRepository : IPaymentRepository
{
    private readonly PlatformDbContext _context;

    public PaymentRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Payment.Domain.Payment?> GetByIdAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken)
    {
        return await _context.Payments
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == paymentId, cancellationToken);
    }

    public async Task<Payment.Domain.Payment?> GetByIdWithDetailsAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken)
    {
        return await _context.Payments
            .Include(p => p.Attempts)
            .Include(p => p.StatusHistories)
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == paymentId, cancellationToken);
    }

    public async Task<Payment.Domain.Payment?> GetByIdForUpdateAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken)
    {
        return await _context.Payments
            .FromSqlInterpolated($"SELECT * FROM platform.payments WHERE \"TenantId\" = {tenantId} AND \"Id\" = {paymentId} FOR UPDATE")
            .Include(p => p.Attempts)
            .Include(p => p.StatusHistories)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Payment.Domain.Payment?> GetByOrderIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        return await _context.Payments
            .Include(p => p.Attempts)
            .Include(p => p.StatusHistories)
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.OrderId == orderId, cancellationToken);
    }

    public async Task<PaymentAttempt?> GetAttemptByIdAsync(Guid tenantId, Guid paymentId, Guid attemptId, CancellationToken cancellationToken)
    {
        return await _context.PaymentAttempts
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.PaymentId == paymentId && a.Id == attemptId, cancellationToken);
    }

    public async Task AddAsync(Payment.Domain.Payment payment, CancellationToken cancellationToken)
    {
        await _context.Payments.AddAsync(payment, cancellationToken);
    }

    public async Task AddAttemptAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        await _context.PaymentAttempts.AddAsync(attempt, cancellationToken);
    }

    public async Task AddStatusHistoryAsync(PaymentStatusHistory history, CancellationToken cancellationToken)
    {
        await _context.PaymentStatusHistories.AddAsync(history, cancellationToken);
    }

    public async Task<int> GetNextAttemptNumberAsync(Guid tenantId, Guid paymentId, CancellationToken cancellationToken)
    {
        var maxAttempt = await _context.PaymentAttempts
            .Where(a => a.TenantId == tenantId && a.PaymentId == paymentId)
            .MaxAsync(a => (int?)a.AttemptNumber, cancellationToken);

        return (maxAttempt ?? 0) + 1;
    }

    public async Task<bool> HasActivePaymentForOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        return await _context.Payments
            .AnyAsync(p => p.TenantId == tenantId && p.OrderId == orderId, cancellationToken);
    }

    public async Task<(IReadOnlyList<Payment.Domain.Payment> Items, int TotalCount)> ListAsync(
        Guid tenantId,
        PaymentListFilter filter,
        CancellationToken cancellationToken)
    {
        var query = _context.Payments
            .Where(p => p.TenantId == tenantId);

        if (filter.Status.HasValue)
        {
            query = query.Where(p => p.Status == filter.Status.Value);
        }

        if (filter.Method.HasValue)
        {
            query = query.Where(p => p.Method == filter.Method.Value);
        }

        if (filter.OrderId.HasValue)
        {
            query = query.Where(p => p.OrderId == filter.OrderId.Value);
        }

        if (filter.FromDate.HasValue)
        {
            query = query.Where(p => p.CreatedAt >= filter.FromDate.Value);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(p => p.CreatedAt <= filter.ToDate.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                var result = await operation();
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                throw new ConflictException("A concurrent update conflicted with this payment operation.");
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                throw new ConflictException("A concurrent operation conflicted with this payment constraint.");
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
        {
            return true;
        }

        return false;
    }
}
