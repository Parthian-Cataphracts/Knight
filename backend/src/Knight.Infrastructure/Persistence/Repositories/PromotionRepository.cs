using Microsoft.EntityFrameworkCore;
using Promotions.Domain;

namespace Knight.Infrastructure.Persistence.Repositories;

public sealed class PromotionRepository : IPromotionRepository
{
    private readonly PlatformDbContext _dbContext;

    public PromotionRepository(PlatformDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Promotion?> GetByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        return _dbContext.Promotions
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, cancellationToken);
    }

    public async Task<(IReadOnlyList<Promotion> Items, int TotalCount)> ListPromotionsAsync(
        Guid tenantId,
        PromotionListFilter filter,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.Promotions.Where(p => p.TenantId == tenantId);

        if (filter.Status.HasValue)
        {
            query = query.Where(p => p.Status == filter.Status.Value);
        }

        if (filter.DiscountType.HasValue)
        {
            query = query.Where(p => p.DiscountType == filter.DiscountType.Value);
        }

        if (filter.RequiresCoupon.HasValue)
        {
            query = query.Where(p => p.RequiresCoupon == filter.RequiresCoupon.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddPromotionAsync(Promotion promotion, CancellationToken cancellationToken)
    {
        await _dbContext.Promotions.AddAsync(promotion, cancellationToken);
    }

    public Task<Coupon?> GetCouponByIdAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        return _dbContext.Coupons
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id, cancellationToken);
    }

    public Task<Coupon?> GetCouponByIdForUpdateAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        return _dbContext.Coupons
            .FromSqlInterpolated($"SELECT * FROM platform.coupons WHERE \"TenantId\" = {tenantId} AND \"Id\" = {id} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<Coupon?> GetCouponByNormalizedCodeAsync(Guid tenantId, string normalizedCode, CancellationToken cancellationToken)
    {
        return _dbContext.Coupons
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.NormalizedCode == normalizedCode, cancellationToken);
    }

    public Task<Coupon?> GetCouponByNormalizedCodeForUpdateAsync(Guid tenantId, string normalizedCode, CancellationToken cancellationToken)
    {
        return _dbContext.Coupons
            .FromSqlInterpolated($"SELECT * FROM platform.coupons WHERE \"TenantId\" = {tenantId} AND \"NormalizedCode\" = {normalizedCode} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Coupon> Items, int TotalCount)> ListCouponsAsync(
        Guid tenantId,
        CouponListFilter filter,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.Coupons.Where(c => c.TenantId == tenantId);

        if (filter.PromotionId.HasValue)
        {
            query = query.Where(c => c.PromotionId == filter.PromotionId.Value);
        }

        if (filter.Status.HasValue)
        {
            query = query.Where(c => c.Status == filter.Status.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddCouponAsync(Coupon coupon, CancellationToken cancellationToken)
    {
        await _dbContext.Coupons.AddAsync(coupon, cancellationToken);
    }

    public Task<int> GetCouponRedemptionCountAsync(Guid tenantId, Guid couponId, CancellationToken cancellationToken)
    {
        return _dbContext.CouponRedemptions
            .CountAsync(r => r.TenantId == tenantId && r.CouponId == couponId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetCouponRedemptionCountsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> couponIds,
        CancellationToken cancellationToken)
    {
        if (couponIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var counts = await _dbContext.CouponRedemptions
            .Where(r => r.TenantId == tenantId && couponIds.Contains(r.CouponId))
            .GroupBy(r => r.CouponId)
            .Select(g => new { CouponId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(c => c.CouponId, c => c.Count);
    }

    public Task<bool> HasOrderRedeemedCouponAsync(Guid tenantId, Guid couponId, Guid orderId, CancellationToken cancellationToken)
    {
        return _dbContext.CouponRedemptions
            .AnyAsync(r => r.TenantId == tenantId && r.CouponId == couponId && r.OrderId == orderId, cancellationToken);
    }

    public async Task AddCouponRedemptionAsync(CouponRedemption redemption, CancellationToken cancellationToken)
    {
        await _dbContext.CouponRedemptions.AddAsync(redemption, cancellationToken);
    }

    public async Task<IReadOnlyList<Promotion>> GetActiveAutomaticPromotionsAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await _dbContext.Promotions
            .Where(p => p.TenantId == tenantId &&
                        p.Status == PromotionStatus.Active &&
                        !p.RequiresCoupon &&
                        (!p.StartsAt.HasValue || p.StartsAt <= now) &&
                        (!p.EndsAt.HasValue || now < p.EndsAt))
            .ToListAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            return await operation();
        }

        var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await operation();
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }
}
