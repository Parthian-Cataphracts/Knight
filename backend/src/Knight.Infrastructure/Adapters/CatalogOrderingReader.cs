using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Ordering.Domain;
using Knight.Infrastructure.Persistence;

namespace Knight.Infrastructure.Adapters;

internal sealed class CatalogOrderingReader : ICatalogOrderingReader
{
    private readonly PlatformDbContext _context;

    public CatalogOrderingReader(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<Guid, OrderableProductSnapshot>> GetOrderableProductsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, OrderableProductSnapshot>();
        }

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && productIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        if (products.Count == 0)
        {
            return new Dictionary<Guid, OrderableProductSnapshot>();
        }

        var matchedProductIds = products.Select(p => p.Id).ToList();

        var variants = await _context.ProductVariants
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId && matchedProductIds.Contains(v.ProductId))
            .ToListAsync(cancellationToken);

        var productModifierGroups = await _context.ProductModifierGroups
            .AsNoTracking()
            .Where(pmg => pmg.TenantId == tenantId && matchedProductIds.Contains(pmg.ProductId))
            .ToListAsync(cancellationToken);

        var modifierGroupIds = productModifierGroups.Select(pmg => pmg.ModifierGroupId).Distinct().ToList();

        var modifierGroups = modifierGroupIds.Count > 0
            ? await _context.ModifierGroups
                .AsNoTracking()
                .Where(mg => mg.TenantId == tenantId && modifierGroupIds.Contains(mg.Id))
                .ToListAsync(cancellationToken)
            : [];

        var modifiers = modifierGroupIds.Count > 0
            ? await _context.Modifiers
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && modifierGroupIds.Contains(m.ModifierGroupId))
                .ToListAsync(cancellationToken)
            : [];

        var modifiersByGroup = modifiers
            .GroupBy(m => m.ModifierGroupId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<OrderableModifierSnapshot>)g.Select(m => new OrderableModifierSnapshot(
                    m.Id,
                    m.ModifierGroupId,
                    m.TenantId,
                    m.Name,
                    m.PriceDelta,
                    m.IsAvailable,
                    m.DisplayOrder)).ToList());

        var groupsById = modifierGroups.ToDictionary(g => g.Id, g => g);

        var variantsByProduct = variants
            .GroupBy(v => v.ProductId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<OrderableVariantSnapshot>)g.Select(v => new OrderableVariantSnapshot(
                    v.Id,
                    v.ProductId,
                    v.TenantId,
                    v.Name,
                    v.Price,
                    v.IsAvailable,
                    v.IsDefault)).ToList());

        // Built per (product, group) rather than once per group: the assignment's
        // DisplayOrder is a property of this product's assignment, so the same
        // ModifierGroup can legitimately sit at different positions on two products.
        var groupsByProduct = productModifierGroups
            .GroupBy(pmg => pmg.ProductId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<OrderableModifierGroupSnapshot>)g
                    .Where(pmg => groupsById.ContainsKey(pmg.ModifierGroupId))
                    .Select(pmg =>
                    {
                        var group = groupsById[pmg.ModifierGroupId];
                        return new OrderableModifierGroupSnapshot(
                            group.Id,
                            group.TenantId,
                            group.Name,
                            group.IsRequired,
                            group.MinSelections,
                            group.MaxSelections,
                            pmg.DisplayOrder,
                            group.DisplayOrder,
                            modifiersByGroup.TryGetValue(group.Id, out var mods) ? mods : []);
                    })
                    .ToList());

        var result = new Dictionary<Guid, OrderableProductSnapshot>();

        foreach (var product in products)
        {
            var productVariants = variantsByProduct.TryGetValue(product.Id, out var vars) ? vars : [];
            var productGroups = groupsByProduct.TryGetValue(product.Id, out var grps) ? grps : [];

            result[product.Id] = new OrderableProductSnapshot(
                product.Id,
                product.TenantId,
                product.Name,
                product.BasePrice,
                product.IsVisible,
                product.IsAvailable,
                product.Status.ToString(),
                productVariants,
                productGroups);
        }

        return result;
    }
}
