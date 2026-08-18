using System.Text.Json;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;

namespace AccessControl;

/// <summary>
/// Writes audit entries. Before/after documents are serialised through a
/// redacting pass: any property whose name looks like a credential is replaced
/// with "***" rather than dropped, so the entry still records that the value
/// changed without recording what it changed to
/// (docs/authorization.md section 7).
/// </summary>
internal sealed class AuditTrail : IAuditTrail
{
    private static readonly string[] SensitiveFragments =
    [
        "password", "secret", "token", "credential", "apikey", "api_key", "signature", "privatekey",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IAuditLogRepository _repository;
    private readonly IAuditContext _context;
    private readonly IDateTimeProvider _clock;

    public AuditTrail(IAuditLogRepository repository, IAuditContext context, IDateTimeProvider clock)
    {
        _repository = repository;
        _context = context;
        _clock = clock;
    }

    public async Task RecordAsync(
        string action,
        string targetType,
        string? targetId,
        Guid? customerId,
        CancellationToken cancellationToken,
        object? previousValue = null,
        object? newValue = null)
    {
        var entry = AuditLog.Record(
            Guid.NewGuid(),
            _context.ActorType,
            _context.ActorUserId,
            _context.ActorDisplay,
            customerId,
            action,
            targetType,
            targetId,
            _clock.UtcNow,
            Redact(previousValue),
            Redact(newValue),
            _context.CorrelationId,
            _context.IpAddress);

        await _repository.AddAsync(entry, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }

    private static string? Redact(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var node = JsonSerializer.SerializeToNode(value, SerializerOptions);
        Scrub(node);
        return node?.ToJsonString();
    }

    private static void Scrub(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (IsSensitive(property.Key))
                    {
                        obj[property.Key] = "***";
                        continue;
                    }

                    Scrub(property.Value);
                }

                break;

            case System.Text.Json.Nodes.JsonArray array:
                foreach (var item in array)
                {
                    Scrub(item);
                }

                break;
        }
    }

    private static bool IsSensitive(string propertyName) =>
        SensitiveFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
