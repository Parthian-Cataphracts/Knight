using Ordering.Domain;
using Knight.Application.Exceptions;
using Knight.Domain.Exceptions;

namespace Ordering;

public sealed class OrderPricingCalculator : IOrderPricingCalculator
{
    private const int MaxLineItems = 100;

    private readonly ICatalogOrderingReader _catalogReader;
    private readonly IOrderingTenantReader _tenantReader;
    private readonly IOrderFulfillmentResolver _fulfillmentResolver;
    private readonly IOrderPromotionEvaluator _promotionEvaluator;

    public OrderPricingCalculator(
        ICatalogOrderingReader catalogReader,
        IOrderingTenantReader tenantReader,
        IOrderFulfillmentResolver fulfillmentResolver,
        IOrderPromotionEvaluator promotionEvaluator)
    {
        _catalogReader = catalogReader;
        _tenantReader = tenantReader;
        _fulfillmentResolver = fulfillmentResolver;
        _promotionEvaluator = promotionEvaluator;
    }

    public async Task<OrderPricingCalculationResult> CalculatePricingAsync(
        Guid tenantId,
        IReadOnlyList<PlaceOrderItemInput> items,
        PlaceOrderFulfillmentInput? fulfillment,
        string? couponCode,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["tenantId"] = ["Tenant ID is required."]
            });
        }

        if (items is null || items.Count == 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["items"] = ["An order must contain at least one item."]
            });
        }

        if (items.Count > MaxLineItems)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["items"] = [$"An order cannot contain more than {MaxLineItems} line items."]
            });
        }

        var tenant = await _tenantReader.GetTenantAsync(tenantId, cancellationToken);
        if (tenant is null || !string.Equals(tenant.Status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotFoundException("Tenant", tenantId);
        }

        var productIds = items.Select(i => i.ProductId).Distinct().ToArray();
        var catalogMap = await _catalogReader.GetOrderableProductsAsync(tenantId, productIds, cancellationToken);

        var calculatedItems = new List<CalculatedItemSnapshot>(items.Count);
        var displayOrder = 0;

        foreach (var itemInput in items)
        {
            if (itemInput.Quantity <= 0)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["quantity"] = ["Quantity must be greater than zero."]
                });
            }

            if (!catalogMap.TryGetValue(itemInput.ProductId, out var productSnapshot))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["productId"] = [$"Product '{itemInput.ProductId}' was not found in this tenant."]
                });
            }

            if (!string.Equals(productSnapshot.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["status"] = [$"Product '{productSnapshot.Name}' is not active."]
                });
            }

            if (!productSnapshot.IsVisible)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["isVisible"] = [$"Product '{productSnapshot.Name}' is not visible."]
                });
            }

            if (!productSnapshot.IsAvailable)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["isAvailable"] = [$"Product '{productSnapshot.Name}' is currently unavailable."]
                });
            }

            decimal unitBasePrice;
            Guid? sourceVariantId = null;
            string? variantName = null;

            if (productSnapshot.Variants.Count > 0)
            {
                if (!itemInput.VariantId.HasValue)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["variantId"] = [$"A variant must be selected for product '{productSnapshot.Name}'."]
                    });
                }

                var selectedVariant = productSnapshot.Variants.FirstOrDefault(v => v.Id == itemInput.VariantId.Value);
                if (selectedVariant is null)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["variantId"] = [$"Selected variant is not valid for product '{productSnapshot.Name}'."]
                    });
                }

                if (!selectedVariant.IsAvailable)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["variantId"] = [$"Variant '{selectedVariant.Name}' is currently unavailable."]
                    });
                }

                unitBasePrice = selectedVariant.Price;
                sourceVariantId = selectedVariant.Id;
                variantName = selectedVariant.Name;
            }
            else
            {
                if (itemInput.VariantId.HasValue)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["variantId"] = [$"Product '{productSnapshot.Name}' does not have variants."]
                    });
                }

                unitBasePrice = productSnapshot.BasePrice;
            }

            var selectedModifierIds = itemInput.ModifierIds ?? [];
            if (selectedModifierIds.Count != selectedModifierIds.Distinct().Count())
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["modifierIds"] = ["Duplicate modifier selections are not allowed on a single item."]
                });
            }

            var assignedModifiersMap = productSnapshot.ModifierGroups
                .SelectMany(g => g.Modifiers.Select(m => new { Group = g, Modifier = m }))
                .ToDictionary(x => x.Modifier.Id, x => x);

            var selectedByGroup = new Dictionary<Guid, List<OrderableModifierSnapshot>>();
            var resolvedSelections = new List<(OrderableModifierGroupSnapshot Group, OrderableModifierSnapshot Modifier)>(selectedModifierIds.Count);

            // Validation walks the client's array so failures still report on the
            // first offending id the caller sent; canonical ordering is applied
            // afterwards, once the whole selection is known to be valid.
            foreach (var modId in selectedModifierIds)
            {
                if (!assignedModifiersMap.TryGetValue(modId, out var assignment))
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["modifierIds"] = [$"Modifier '{modId}' is not assigned to product '{productSnapshot.Name}'."]
                    });
                }

                if (!assignment.Modifier.IsAvailable)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["modifierIds"] = [$"Modifier '{assignment.Modifier.Name}' is currently unavailable."]
                    });
                }

                if (!selectedByGroup.TryGetValue(assignment.Group.Id, out var groupList))
                {
                    groupList = [];
                    selectedByGroup[assignment.Group.Id] = groupList;
                }

                groupList.Add(assignment.Modifier);

                resolvedSelections.Add((assignment.Group, assignment.Modifier));
            }

            // Modifier ids are a *set* of selected choices: the client says which
            // modifiers, the server says in what order they are recorded. Ordering is
            // therefore derived entirely from Catalog — the product's group assignment
            // position, then the group's own position, then the modifier's position
            // within its group — with entity ids as a final tie-break so the sort is
            // total and two identical selections can never persist differently.
            // Without this, [A,B] and [B,A] hashed identically (CheckoutRequestHasher
            // sorts ids) but persisted different DisplayOrder values.
            var itemModifiers = resolvedSelections
                .OrderBy(s => s.Group.AssignmentDisplayOrder)
                .ThenBy(s => s.Group.GroupDisplayOrder)
                .ThenBy(s => s.Group.Id)
                .ThenBy(s => s.Modifier.DisplayOrder)
                .ThenBy(s => s.Modifier.Id)
                .Select((s, index) => new CalculatedModifierSnapshot(
                    s.Group.Id,
                    s.Group.Name,
                    s.Modifier.Id,
                    s.Modifier.Name,
                    s.Modifier.PriceDelta,
                    index))
                .ToList();

            foreach (var group in productSnapshot.ModifierGroups)
            {
                var selectedCount = selectedByGroup.TryGetValue(group.Id, out var selected) ? selected.Count : 0;
                var effectiveMin = group.IsRequired ? Math.Max(1, group.MinSelections) : group.MinSelections;

                if (selectedCount < effectiveMin)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["modifiers"] = [$"Modifier group '{group.Name}' requires at least {effectiveMin} selection(s)."]
                    });
                }

                if (group.MaxSelections > 0 && selectedCount > group.MaxSelections)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["modifiers"] = [$"Modifier group '{group.Name}' allows at most {group.MaxSelections} selection(s)."]
                    });
                }
            }

            var unitModifierTotal = itemModifiers.Sum(m => m.PriceDelta);
            var unitPrice = unitBasePrice + unitModifierTotal;
            var lineTotal = unitPrice * itemInput.Quantity;

            var calculatedItem = new CalculatedItemSnapshot(
                productSnapshot.Id,
                productSnapshot.Name,
                sourceVariantId,
                variantName,
                unitBasePrice,
                unitModifierTotal,
                unitPrice,
                itemInput.Quantity,
                lineTotal,
                displayOrder++,
                itemModifiers);

            calculatedItems.Add(calculatedItem);
        }

        var subtotal = calculatedItems.Sum(i => i.LineTotal);

        var promotionResult = await _promotionEvaluator.EvaluateDiscountAsync(
            tenantId,
            subtotal,
            couponCode,
            cancellationToken);

        var discountTotal = promotionResult?.DiscountAmount ?? 0m;
        var discountedSubtotal = Math.Max(0m, subtotal - discountTotal);

        var resolvedFulfillment = await _fulfillmentResolver.ResolveFulfillmentAsync(
            tenantId,
            subtotal,
            fulfillment,
            cancellationToken);

        var fulfillmentFee = resolvedFulfillment?.Fee ?? 0m;
        var total = discountedSubtotal + fulfillmentFee;

        return new OrderPricingCalculationResult(
            tenant.DefaultCurrency,
            subtotal,
            discountTotal,
            discountedSubtotal,
            fulfillmentFee,
            total,
            calculatedItems,
            resolvedFulfillment,
            promotionResult);
    }
}
