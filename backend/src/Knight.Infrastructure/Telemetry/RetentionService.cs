using System.ComponentModel.DataAnnotations;
using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Observability;
using Knight.Infrastructure.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Observability.Domain;

namespace Knight.Infrastructure.Telemetry;

/// <summary>
/// Enforces the retention policy of docs/observability.md §7.
///
/// This is not housekeeping. The tables it trims are the ones that grow with
/// traffic rather than with the number of customers — error events, log lines,
/// health checks, job history — and without it they are the difference between a
/// database that still works next year and one that does not.
///
/// Three rules shape what it will and will not delete:
///
/// * **Audit logs are never touched.** They have a legal minimum and are not
///   operational data (docs/observability.md §7).
/// * **Incidents are never deleted.** An incident is the record of a response,
///   which is the thing a post-mortem is written from a year later.
/// * **Error groups outlive their events.** The events are the bulk; the group
///   is a counter and a title, and deleting it would erase the fact that a
///   problem existed at all. Events go at 30 days, groups at a year — and only
///   groups that are resolved and quiet.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Set false to stop the sweep entirely — for a forensic window, or during an investigation.</summary>
    public bool Enabled { get; init; } = true;

    [Range(typeof(TimeSpan), "01:00:00", "72:00:00")]
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How many rows one pass will delete per table. A single unbounded delete
    /// over a year of accumulated rows takes a lock long enough to be its own
    /// outage; the sweep runs often, so catching up over several passes is fine.
    /// </summary>
    [Range(1000, 1000000)]
    public int BatchSize { get; init; } = 50000;

    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan ErrorEvents { get; init; } = TimeSpan.FromDays(30);

    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan ResolvedErrorGroups { get; init; } = TimeSpan.FromDays(365);

    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan StoreLogs { get; init; } = TimeSpan.FromDays(14);

    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan StoreEvents { get; init; } = TimeSpan.FromDays(90);

    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan StoreHealthChecks { get; init; } = TimeSpan.FromDays(30);

    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan JobHistory { get; init; } = TimeSpan.FromDays(365);

    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan NotificationDeliveries { get; init; } = TimeSpan.FromDays(90);

    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan ResolvedAlerts { get; init; } = TimeSpan.FromDays(90);
}

/// <summary>What one retention pass removed, per table, so it can be logged and asserted on.</summary>
public sealed record RetentionResult(IReadOnlyDictionary<string, int> Deleted)
{
    public int Total => Deleted.Values.Sum();
}

public interface IRetentionService
{
    Task<RetentionResult> ApplyAsync(CancellationToken cancellationToken);
}

internal sealed class RetentionService : IRetentionService
{
    private readonly ControlPlaneDbContext _context;
    private readonly ILogger<RetentionService> _logger;
    private readonly RetentionOptions _options;

    public RetentionService(
        ControlPlaneDbContext context,
        ILogger<RetentionService> logger,
        IOptions<RetentionOptions> options)
    {
        _context = context;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<RetentionResult> ApplyAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var deleted = new Dictionary<string, int>();

        // Set-based deletes throughout: materialising a month of error events to
        // remove them would be its own incident.

        deleted["store_error_events"] = await TrimAsync(
            _context.StoreErrorEvents.Where(error => error.ReceivedAt < now - _options.ErrorEvents),
            cancellationToken);

        deleted["store_log_entries"] = await TrimAsync(
            _context.StoreLogEntries.Where(entry => entry.Timestamp < now - _options.StoreLogs),
            cancellationToken);

        deleted["store_events"] = await TrimAsync(
            _context.StoreEvents.Where(entry => entry.OccurredAt < now - _options.StoreEvents),
            cancellationToken);

        deleted["store_health_checks"] = await TrimAsync(
            _context.StoreHealthChecks.Where(check => check.CheckedAt < now - _options.StoreHealthChecks),
            cancellationToken);

        deleted["notification_deliveries"] = await TrimAsync(
            _context.NotificationDeliveries.Where(delivery =>
                delivery.CreatedAt < now - _options.NotificationDeliveries &&
                delivery.Status != NotificationDeliveryStatus.Pending),
            cancellationToken);

        deleted["alerts"] = await TrimAsync(
            _context.Alerts.Where(alert =>
                alert.ResolvedAt != null && alert.ResolvedAt < now - _options.ResolvedAlerts),
            cancellationToken);

        // Only groups that are both old and settled. A resolved group that is
        // still recurring would be reopened as a regression, and deleting it
        // would lose the history that makes the regression visible.
        deleted["error_groups"] = await TrimAsync(
            _context.ErrorGroups.Where(group =>
                group.LastSeenAt < now - _options.ResolvedErrorGroups &&
                (group.Status == ErrorGroupStatus.Resolved || group.Status == ErrorGroupStatus.Ignored)),
            cancellationToken);

        // Job history is audit-adjacent, so only finished jobs go, and their
        // steps go with them by cascade. A job still queued or running is never
        // touched however old it looks.
        deleted["feature_installation_jobs"] = await TrimAsync(
            _context.FeatureInstallationJobs.Where(job =>
                job.CompletedAt != null && job.CompletedAt < now - _options.JobHistory),
            cancellationToken);

        var result = new RetentionResult(deleted);

        if (result.Total > 0)
        {
            _logger.LogInformation(
                "Retention removed {Total} row(s): {Breakdown}.",
                result.Total,
                string.Join(", ", deleted.Where(pair => pair.Value > 0).Select(pair => $"{pair.Key}={pair.Value}")));
        }

        return result;
    }

    /// <summary>
    /// Deletes at most one batch from the given set.
    ///
    /// The bound is what keeps a first run after a long outage — or a policy
    /// being shortened — from taking a table-wide lock. The sweep runs every few
    /// hours, so catching up across passes costs nothing that matters.
    /// </summary>
    private async Task<int> TrimAsync<TEntity>(IQueryable<TEntity> expired, CancellationToken cancellationToken)
        where TEntity : class
    {
        try
        {
            return await expired
                .IgnoreQueryFilters()
                .Take(_options.BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One table failing must not stop the others: the table that fails is
            // usually the one under most pressure, which is exactly the one whose
            // neighbours most need trimming.
            _logger.LogError(exception, "Retention failed for {Entity}; the remaining tables are still swept.", typeof(TEntity).Name);

            return 0;
        }
    }
}

/// <summary>
/// Registers the pieces of self-telemetry that must see the schema.
///
/// They stay internal: nothing outside this assembly should be able to run a
/// delete sweep or read the gauge queries directly.
/// </summary>
public static class TelemetryInfrastructure
{
    public static IServiceCollection AddKnightTelemetryInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IObservabilityGaugeSource, ObservabilityGaugeSource>();
        services.AddScoped<IRetentionService, RetentionService>();

        return services;
    }
}
