using Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class ProductModifierGroupRepository : IProductModifierGroupRepository
{
    private readonly PlatformDbContext _context;

    public ProductModifierGroupRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyCollection<ProductModifierGroup>> ListByProductAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        await _context.ProductModifierGroups
            .Where(a => a.TenantId == tenantId && a.ProductId == productId)
            .OrderBy(a => a.DisplayOrder)
            .ThenBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .ToArrayAsync(cancellationToken);

    public async Task ReplaceForProductAsync(
        Guid tenantId,
        Guid productId,
        IReadOnlyCollection<(Guid ModifierGroupId, int DisplayOrder)> assignments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.ProductModifierGroups
            .Where(a => a.TenantId == tenantId && a.ProductId == productId)
            .ExecuteDeleteAsync(cancellationToken);

        if (assignments.Count > 0)
        {
            var rows = assignments.Select(assignment => ProductModifierGroup.Create(
                Guid.NewGuid(),
                now,
                tenantId,
                productId,
                assignment.ModifierGroupId,
                assignment.DisplayOrder));

            await _context.ProductModifierGroups.AddRangeAsync(rows, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
