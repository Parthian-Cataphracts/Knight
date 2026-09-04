using Knight.Application.Abstractions.ControlPlane;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// The deprovisioning Export step's automation (hardening backlog P3): a
/// deprovisioning run produces the customer's export itself, before purge, with no
/// operator. This exercises the exporter the step calls.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StoreExportTests
{
    private readonly PostgresApiFixture _fixture;

    public StoreExportTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TheExporterWritesTheCustomersRecordToADurableFile()
    {
        if (!_fixture.IsAvailable) return;

        var customerId = await _fixture.SeedCustomerAsync();
        var storeId = await _fixture.SeedStoreAsync(customerId);

        var record = await _fixture.WithControlPlaneScopeAsync(async (_, services) =>
            await services.GetRequiredService<IStoreExporter>().ExportAsync(storeId, CancellationToken.None));

        try
        {
            Assert.True(File.Exists(record.Location), $"the export file {record.Location} should exist");
            Assert.True(record.SizeBytes > 0);

            var content = await File.ReadAllTextAsync(record.Location);
            Assert.Contains(customerId.ToString(), content, StringComparison.Ordinal);
            Assert.Contains(storeId.ToString(), content, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(record.Location))
            {
                File.Delete(record.Location);
            }
        }
    }
}
