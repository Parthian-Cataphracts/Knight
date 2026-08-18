using Fulfillment.Domain;
using Knight.Application.Abstractions.Time;

namespace Fulfillment;

public interface IFulfillmentManagementService
{
    Task<TenantFulfillmentSettings> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<TenantFulfillmentSettings> UpdateSettingsAsync(Guid tenantId, bool pickupEnabled, CancellationToken cancellationToken = default);
}

public sealed class FulfillmentManagementService : IFulfillmentManagementService
{
    private readonly ITenantFulfillmentSettingsRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly FulfillmentAuditRecorder _audit;

    public FulfillmentManagementService(
        ITenantFulfillmentSettingsRepository repository,
        IDateTimeProvider dateTimeProvider,
        FulfillmentAuditRecorder audit)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public async Task<TenantFulfillmentSettings> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var settings = await _repository.GetByTenantIdAsync(tenantId, cancellationToken);
        if (settings is null)
        {
            // Default active pickup settings if not persisted yet
            return TenantFulfillmentSettings.Create(tenantId, _dateTimeProvider.UtcNow, pickupEnabled: true);
        }

        return settings;
    }

    public async Task<TenantFulfillmentSettings> UpdateSettingsAsync(Guid tenantId, bool pickupEnabled, CancellationToken cancellationToken = default)
    {
        var now = _dateTimeProvider.UtcNow;
        var settings = await _repository.GetByTenantIdAsync(tenantId, cancellationToken);

        if (settings is null)
        {
            settings = TenantFulfillmentSettings.Create(tenantId, now, pickupEnabled);
            await _repository.AddAsync(settings, cancellationToken);
        }
        else
        {
            settings.Update(pickupEnabled, now);
        }

        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "FulfillmentSettingsUpdated",
            tenantId,
            cancellationToken,
            nonPiiMetadata: new Dictionary<string, string>
            {
                ["pickupEnabled"] = pickupEnabled.ToString()
            });

        return settings;
    }
}
