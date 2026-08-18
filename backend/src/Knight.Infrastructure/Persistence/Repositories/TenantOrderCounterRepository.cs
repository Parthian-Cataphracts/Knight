using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class TenantOrderCounterRepository : ITenantOrderCounterRepository
{
    private readonly PlatformDbContext _context;

    public TenantOrderCounterRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<long> NextOrderNumberAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO platform.tenant_order_counters ("TenantId", "NextOrderNumber")
            VALUES (@tenantId, 1002)
            ON CONFLICT ("TenantId")
            DO UPDATE SET "NextOrderNumber" = platform.tenant_order_counters."NextOrderNumber" + 1
            RETURNING platform.tenant_order_counters."NextOrderNumber" - 1;
            """;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenantId";
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);

        if (_context.Database.CurrentTransaction is not null)
        {
            command.Transaction = _context.Database.CurrentTransaction.GetDbTransaction();
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }
}
