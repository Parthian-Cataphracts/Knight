using Catalog.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Catalog;

public sealed class ProductModifierAssignmentService : IProductModifierAssignmentService
{
    private const string EntityType = nameof(Product);

    private readonly IProductModifierGroupRepository _repository;
    private readonly IProductRepository _productRepository;
    private readonly IModifierGroupRepository _modifierGroupRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly CatalogAuditRecorder _audit;

    public ProductModifierAssignmentService(
        IProductModifierGroupRepository repository,
        IProductRepository productRepository,
        IModifierGroupRepository modifierGroupRepository,
        IDateTimeProvider dateTimeProvider,
        CatalogAuditRecorder audit)
    {
        _repository = repository;
        _productRepository = productRepository;
        _modifierGroupRepository = modifierGroupRepository;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public Task<IReadOnlyCollection<ProductModifierGroup>> ListAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        _repository.ListByProductAsync(tenantId, productId, cancellationToken);

    public async Task ReplaceAsync(Guid tenantId, Guid productId, IReadOnlyCollection<ProductModifierGroupAssignment> assignments, CancellationToken cancellationToken)
    {
        var product = await _productRepository.GetByIdAsync(tenantId, productId, cancellationToken)
            ?? throw new NotFoundException(nameof(Product), productId);

        var distinct = assignments
            .GroupBy(a => a.ModifierGroupId)
            .Select(g => g.First())
            .ToArray();

        if (distinct.Length != assignments.Count)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["assignments"] = ["The same modifier group cannot be assigned to a product more than once."]
            });
        }

        foreach (var assignment in distinct)
        {
            var group = await _modifierGroupRepository.GetByIdAsync(tenantId, assignment.ModifierGroupId, cancellationToken);
            if (group is null)
            {
                throw new NotFoundException(nameof(ModifierGroup), assignment.ModifierGroupId);
            }
        }

        await _repository.ReplaceForProductAsync(
            tenantId,
            product.Id,
            distinct.Select(a => (a.ModifierGroupId, a.DisplayOrder)).ToArray(),
            _dateTimeProvider.UtcNow,
            cancellationToken);

        await _audit.RecordAsync("ModifierConfigurationChanged", tenantId, EntityType, product.Id, cancellationToken, new Dictionary<string, string>
        {
            ["operation"] = "ProductAssignmentsReplaced",
            ["assignmentCount"] = distinct.Length.ToString()
        });
    }
}
