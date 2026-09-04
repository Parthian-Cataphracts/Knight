using System.Text.Json;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Provisioning;

namespace Knight.Infrastructure.ControlPlane.Adapters;

/// <summary>
/// Produces the durable export a deprovisioning run makes before it purges
/// anything (hardening backlog P3). It reuses <see cref="ITenantExportReader"/> —
/// the same record the customer can pull from <c>/me/export</c> — and writes a
/// snapshot to durable storage so it outlives the store's archival and the
/// customer's self-serve access.
///
/// A local directory here, honest about what it is: a real deployment points
/// <c>Provisioning:ExportRoot</c> at object storage, the same move the artifact
/// store makes.
/// </summary>
internal sealed class StoreExporter : IStoreExporter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ControlPlaneDbContext _context;
    private readonly ITenantExportReader _export;
    private readonly ProvisioningOptions _options;

    public StoreExporter(ControlPlaneDbContext context, ITenantExportReader export, IOptions<ProvisioningOptions> options)
    {
        _context = context;
        _export = export;
        _options = options.Value;
    }

    public async Task<StoreExportRecord> ExportAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var customerId = await _context.Stores
            .AsNoTracking()
            .Where(store => store.Id == storeId)
            .Select(store => store.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (customerId == Guid.Empty)
        {
            throw new InvalidOperationException($"Store '{storeId}' has no customer to export.");
        }

        var document = await _export.ExportAsync(customerId, cancellationToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);

        var root = Path.GetFullPath(_options.ExportRoot);
        Directory.CreateDirectory(root);
        var location = Path.Combine(root, $"{storeId:n}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");

        await File.WriteAllBytesAsync(location, bytes, cancellationToken);

        return new StoreExportRecord(location, bytes.LongLength);
    }
}
