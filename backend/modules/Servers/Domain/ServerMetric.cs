using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Servers.Domain;

/// <summary>
/// One sample of a server's resource use (docs/domain-model.md §7).
///
/// Append-only and the highest-volume table in the control plane: one row per
/// server per interval, forever, unless something deletes them. The retention job
/// is not a nicety here, it is the difference between a database that works next
/// year and one that does not — which is why the table exists with a retention
/// policy from the first commit (docs/observability.md §7).
///
/// Percentages are stored as they were reported and totals as bytes. Nothing is
/// derived on the way in: a dashboard that wants "percent of disk used" can divide,
/// and a stored derivation would be a number nobody can check against the source.
/// </summary>
public sealed class ServerMetric : Entity
{
    public Guid ServerId { get; private set; }

    public DateTimeOffset CapturedAt { get; private set; }

    public double CpuPercent { get; private set; }

    public long MemoryUsedBytes { get; private set; }

    public long MemoryTotalBytes { get; private set; }

    public long DiskUsedBytes { get; private set; }

    public long DiskTotalBytes { get; private set; }

    public long NetInBytes { get; private set; }

    public long NetOutBytes { get; private set; }

    /// <summary>One-minute load average where the platform reports one; null on those that do not.</summary>
    public double? LoadAverage { get; private set; }

    private ServerMetric()
    {
    }

    private ServerMetric(
        Guid id,
        Guid serverId,
        DateTimeOffset capturedAt,
        double cpuPercent,
        long memoryUsedBytes,
        long memoryTotalBytes,
        long diskUsedBytes,
        long diskTotalBytes,
        long netInBytes,
        long netOutBytes,
        double? loadAverage)
        : base(id)
    {
        ServerId = serverId;
        CapturedAt = capturedAt;
        CpuPercent = cpuPercent;
        MemoryUsedBytes = memoryUsedBytes;
        MemoryTotalBytes = memoryTotalBytes;
        DiskUsedBytes = diskUsedBytes;
        DiskTotalBytes = diskTotalBytes;
        NetInBytes = netInBytes;
        NetOutBytes = netOutBytes;
        LoadAverage = loadAverage;
    }

    public static ServerMetric Capture(
        Guid id,
        Guid serverId,
        DateTimeOffset capturedAt,
        double cpuPercent,
        long memoryUsedBytes,
        long memoryTotalBytes,
        long diskUsedBytes,
        long diskTotalBytes,
        long netInBytes = 0,
        long netOutBytes = 0,
        double? loadAverage = null)
    {
        if (serverId == Guid.Empty)
        {
            throw DomainException.Validation("A metric must belong to a server.");
        }

        // Clamped rather than refused. A sample that says 101% CPU is a rounding
        // artefact from the agent's platform, and throwing away the whole sample
        // — losing the memory and disk figures with it — would be a worse answer
        // than recording a slightly blunt one.
        var cpu = Math.Clamp(cpuPercent, 0, 100);

        return new ServerMetric(
            id,
            serverId,
            capturedAt,
            cpu,
            NonNegative(memoryUsedBytes, "memory used"),
            NonNegative(memoryTotalBytes, "memory total"),
            NonNegative(diskUsedBytes, "disk used"),
            NonNegative(diskTotalBytes, "disk total"),
            NonNegative(netInBytes, "network in"),
            NonNegative(netOutBytes, "network out"),
            loadAverage is null ? null : Math.Max(0, loadAverage.Value));
    }

    /// <summary>Memory in use as a percentage, or null when the agent did not report a total.</summary>
    public double? MemoryPercent => MemoryTotalBytes > 0 ? MemoryUsedBytes * 100d / MemoryTotalBytes : null;

    public double? DiskPercent => DiskTotalBytes > 0 ? DiskUsedBytes * 100d / DiskTotalBytes : null;

    private static long NonNegative(long value, string what) =>
        value >= 0 ? value : throw DomainException.Validation($"The {what} figure cannot be negative.");
}
