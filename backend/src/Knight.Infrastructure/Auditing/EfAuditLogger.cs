using System.Text.Json;
using Knight.Application.Abstractions.Auditing;
using Knight.Infrastructure.Persistence;

namespace Knight.Infrastructure.Auditing;

internal sealed class EfAuditLogger : IAuditLogger
{
    private readonly PlatformDbContext _context;

    public EfAuditLogger(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        var record = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            ActorUserId = entry.ActorUserId,
            ActorType = entry.ActorType.ToString(),
            TenantId = entry.TenantId,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            MetadataJson = entry.Metadata is null ? null : JsonSerializer.Serialize(entry.Metadata),
            OccurredAt = DateTimeOffset.UtcNow
        };

        await _context.AuditLogEntries.AddAsync(record, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
