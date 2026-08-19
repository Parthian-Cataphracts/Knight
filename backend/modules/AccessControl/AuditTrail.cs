using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Observability;
using Knight.Application.Abstractions.Time;

namespace AccessControl;

/// <summary>
/// Writes audit entries. Before/after documents go through the central
/// redacting pass: any property whose name looks like a credential is replaced
/// with "***" rather than dropped, so the entry still records that the value
/// changed without recording what it changed to
/// (docs/authorization.md section 7).
///
/// The redaction itself lives in <see cref="Redaction"/> rather than here,
/// because the log stream and agent job output need exactly the same guarantee
/// and a second implementation is a second chance to get it wrong.
/// </summary>
internal sealed class AuditTrail : IAuditTrail
{
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
            Redaction.Document(previousValue),
            Redaction.Document(newValue),
            _context.CorrelationId,
            _context.IpAddress);

        await _repository.AddAsync(entry, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }
}
