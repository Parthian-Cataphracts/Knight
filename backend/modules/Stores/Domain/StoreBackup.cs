using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Stores.Domain;

/// <summary>
/// One backup a store says it took.
///
/// KNIGHT does not take backups and does not hold them. A store's data is the
/// store's, and its database is somewhere KNIGHT deliberately cannot reach
/// (docs/README.md, rules 1 and 3). What KNIGHT can do is insist on being told:
/// a backup that nobody reported is, from the control plane's point of view,
/// a backup that did not happen — and that is the honest reading, because the
/// alternative is a dashboard that shows green for a store whose backup job died
/// three weeks ago.
///
/// The location is a reference an operator can act on — a bucket key, a volume
/// name — never a URL with credentials in it. This row is read by support staff
/// long after the backup expired.
/// </summary>
public sealed class StoreBackup : Entity, ICustomerOwned
{
    public const int MaxLocationLength = 500;

    public Guid StoreId { get; private set; }

    public Guid CustomerId { get; private set; }

    public BackupStatus Status { get; private set; }

    public BackupKind Kind { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When KNIGHT was told. Distinct from when the backup ran: a store that could not reach KNIGHT reports late.</summary>
    public DateTimeOffset ReportedAt { get; private set; }

    public long? SizeBytes { get; private set; }

    /// <summary>Where the store put it, as a reference. Never a credential-bearing URL.</summary>
    public string? Location { get; private set; }

    /// <summary>Why it failed, in one line, redacted and capped like every other store-supplied text.</summary>
    public string? Detail { get; private set; }

    public TimeSpan? Duration => CompletedAt is { } completed ? completed - StartedAt : null;

    private StoreBackup()
    {
    }

    private StoreBackup(
        Guid id,
        Guid storeId,
        Guid customerId,
        BackupStatus status,
        BackupKind kind,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        DateTimeOffset reportedAt,
        long? sizeBytes,
        string? location,
        string? detail)
        : base(id)
    {
        StoreId = storeId;
        CustomerId = customerId;
        Status = status;
        Kind = kind;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ReportedAt = reportedAt;
        SizeBytes = sizeBytes;
        Location = location;
        Detail = detail;
    }

    public static StoreBackup Record(
        Guid id,
        Guid storeId,
        Guid customerId,
        BackupStatus status,
        BackupKind kind,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        DateTimeOffset reportedAt,
        long? sizeBytes = null,
        string? location = null,
        string? detail = null)
    {
        if (storeId == Guid.Empty)
        {
            throw DomainException.Validation("A backup report must belong to a store.");
        }

        if (completedAt is { } completed && completed < startedAt)
        {
            throw DomainException.Validation("A backup cannot finish before it started.");
        }

        if (sizeBytes is < 0)
        {
            throw DomainException.Validation("A backup size cannot be negative.");
        }

        // A backup reported as succeeded with no size is refused rather than
        // recorded: "it worked and produced nothing" is the exact shape of a
        // silently broken backup job, and accepting it would show green.
        if (status is BackupStatus.Succeeded && sizeBytes is null or 0)
        {
            throw DomainException.Validation("A successful backup must report the size it produced.");
        }

        return new StoreBackup(
            id,
            storeId,
            customerId,
            status,
            kind,
            startedAt,
            completedAt,
            reportedAt,
            sizeBytes,
            Truncate(location, MaxLocationLength),
            Truncate(detail, 1000));
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim() is var trimmed && trimmed.Length <= max ? trimmed : value.Trim()[..max];
}

public enum BackupStatus
{
    Succeeded = 0,
    Failed = 1,

    /// <summary>Reported as started. A run that never reports again shows up as overdue, which is the point.</summary>
    Running = 2,
}

public enum BackupKind
{
    Scheduled = 0,
    Manual = 1,

    /// <summary>Taken by the agent before an install or upgrade touched the database.</summary>
    PreDeployment = 2,
}
