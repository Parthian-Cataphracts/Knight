using Knight.Contracts.Promotions;
using Promotions.Domain;

namespace Promotions;

public interface IPromotionManagementService
{
    Task<PromotionResponse> CreatePromotionAsync(
        Guid tenantId,
        CreatePromotionRequest request,
        CancellationToken cancellationToken);

    Task<PromotionResponse> UpdatePromotionAsync(
        Guid tenantId,
        Guid id,
        UpdatePromotionRequest request,
        CancellationToken cancellationToken);

    Task<PromotionResponse> ActivatePromotionAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken);

    Task<PromotionResponse> ArchivePromotionAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken);

    Task<PromotionResponse> GetPromotionByIdAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<PromotionResponse> Items, int TotalCount)> ListPromotionsAsync(
        Guid tenantId,
        PromotionListFilter filter,
        CancellationToken cancellationToken);
}
