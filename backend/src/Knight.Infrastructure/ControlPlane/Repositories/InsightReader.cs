using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Observability.Domain;
using Servers.Domain;
using Stores.Domain;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// The projections behind the dashboard's summary panels.
///
/// Everything here is a read across module boundaries, which is why it lives in
/// Infrastructure rather than in any one module (docs/README.md, rule 3). The
/// customer-scoped reads go through the context's isolation filter like every
/// other query, so a customer principal asking for "activity" gets theirs and a
/// platform principal gets whatever they asked for.
/// </summary>
internal sealed class InsightReader : IInsightReader
{
    private readonly ControlPlaneDbContext _context;
    private readonly HealthCheckService _health;

    public InsightReader(ControlPlaneDbContext context, HealthCheckService health)
    {
        _context = context;
        _health = health;
    }

    /// <summary>
    /// The platform's dependencies, reported from the same health checks the
    /// readiness probe uses.
    ///
    /// Deriving it from the real probe rather than from a hand-written list is
    /// the point: a screen that says "database: healthy" from a different source
    /// than the one deciding whether to route traffic will eventually disagree
    /// with it, and the screen is the one people believe.
    /// </summary>
    public async Task<IReadOnlyCollection<PlatformServiceStatus>> ReadServicesAsync(CancellationToken cancellationToken)
    {
        var report = await _health.CheckHealthAsync(cancellationToken);
        var services = new List<PlatformServiceStatus>();

        foreach (var (name, entry) in report.Entries)
        {
            services.Add(new PlatformServiceStatus(
                name,
                Humanise(name),
                entry.Description ?? entry.Exception?.Message ?? "Reporting normally.",
                Translate(entry.Status),
                [
                    new KeyValuePair<string, string>("latency", $"{entry.Duration.TotalMilliseconds:0} ms"),
                ]));
        }

        // The control plane's own moving parts, which no health check covers
        // because they are queues rather than dependencies: a job queue that
        // only grows and a notification backlog are both failures that leave
        // every dependency looking perfectly healthy.
        var queuedJobs = await _context.FeatureInstallationJobs
            .AsNoTracking()
            .CountAsync(job => job.State == JobState.Queued, cancellationToken);

        var runningJobs = await _context.FeatureInstallationJobs
            .AsNoTracking()
            .CountAsync(job => job.State == JobState.Running, cancellationToken);

        var pendingNotifications = await _context.NotificationDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.Status == NotificationDeliveryStatus.Pending, cancellationToken);

        var failedNotifications = await _context.NotificationDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.Status == NotificationDeliveryStatus.Failed, cancellationToken);

        services.Add(new PlatformServiceStatus(
            "delivery-queue",
            "Feature delivery queue",
            queuedJobs == 0 ? "Nothing waiting." : $"{queuedJobs} job(s) waiting for an agent to claim them.",
            queuedJobs > 50 ? "Degraded" : "Healthy",
            [
                new KeyValuePair<string, string>("queued", queuedJobs.ToString()),
                new KeyValuePair<string, string>("running", runningJobs.ToString()),
            ]));

        services.Add(new PlatformServiceStatus(
            "notifications",
            "Notification dispatch",
            failedNotifications == 0
                ? "Delivering normally."
                : $"{failedNotifications} delivery attempt(s) have been abandoned.",
            failedNotifications > 0 ? "Degraded" : "Healthy",
            [
                new KeyValuePair<string, string>("pending", pendingNotifications.ToString()),
                new KeyValuePair<string, string>("failed", failedNotifications.ToString()),
            ]));

        return services;
    }

    /// <summary>
    /// The reports KNIGHT can produce, each stamped with the age of the data
    /// behind it.
    ///
    /// A null timestamp means there is no data yet, and says so rather than
    /// showing a date that would imply the report is ready.
    /// </summary>
    public async Task<IReadOnlyCollection<ReportSummary>> ReadReportsAsync(CancellationToken cancellationToken)
    {
        var lastError = await _context.ErrorGroups
            .AsNoTracking()
            .OrderByDescending(group => group.LastSeenAt)
            .Select(group => (DateTimeOffset?)group.LastSeenAt)
            .FirstOrDefaultAsync(cancellationToken);

        var lastJob = await _context.FeatureInstallationJobs
            .AsNoTracking()
            .Where(job => job.CompletedAt != null)
            .OrderByDescending(job => job.CompletedAt)
            .Select(job => job.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var lastInvoice = await _context.Invoices
            .AsNoTracking()
            .OrderByDescending(invoice => invoice.CreatedAt)
            .Select(invoice => (DateTimeOffset?)invoice.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var lastCheck = await _context.StoreHealthChecks
            .AsNoTracking()
            .OrderByDescending(check => check.CheckedAt)
            .Select(check => (DateTimeOffset?)check.CheckedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return
        [
            new ReportSummary(
                "error-volume",
                "Error volume by store",
                "Grouped errors over time, by store and environment, with the problems that recurred after being resolved.",
                lastError),

            new ReportSummary(
                "delivery-throughput",
                "Feature delivery throughput",
                "Installation jobs by type and outcome, how long they took, and how many needed a rollback.",
                lastJob),

            new ReportSummary(
                "store-availability",
                "Store availability",
                "Health-probe results per store: uptime, latency, and the periods each store was unreachable.",
                lastCheck),

            new ReportSummary(
                "billing-summary",
                "Billing summary",
                "Invoices by state, outstanding totals, and payments recorded against them.",
                lastInvoice),
        ];
    }

    public async Task<IReadOnlyCollection<EntitlementMatrixRow>> ReadEntitlementMatrixAsync(
        CancellationToken cancellationToken)
    {
        var plans = await _context.Plans
            .AsNoTracking()
            .Include(plan => plan.Features)
            .OrderBy(plan => plan.Key)
            .ToArrayAsync(cancellationToken);

        if (plans.Length == 0)
        {
            return [];
        }

        var featureIds = plans
            .SelectMany(plan => plan.Features.Select(feature => feature.FeatureId))
            .Distinct()
            .ToArray();

        var features = await _context.Features
            .AsNoTracking()
            .Where(feature => featureIds.Contains(feature.Id))
            .Select(feature => new { feature.Id, feature.Slug, feature.Name })
            .ToArrayAsync(cancellationToken);

        return features
            .OrderBy(feature => feature.Slug)
            .Select(feature => new EntitlementMatrixRow(
                feature.Slug,
                feature.Name,
                plans.ToDictionary(
                    plan => plan.Key,
                    plan =>
                    {
                        var included = plan.Features.FirstOrDefault(entry => entry.FeatureId == feature.Id);

                        // The pinned range is shown where there is one: "which
                        // plans include this" and "at which version" are the two
                        // questions this screen exists to answer.
                        return included is null
                            ? "—"
                            : string.IsNullOrWhiteSpace(included.PinnedVersionRange)
                                ? "yes"
                                : included.PinnedVersionRange;
                    })))
            .ToArray();
    }

    public async Task<IReadOnlyCollection<ActivityItem>> ReadCustomerActivityAsync(
        Guid customerId,
        int limit,
        CancellationToken cancellationToken)
    {
        var entries = await _context.AuditLogs
            .AsNoTracking()
            .Where(entry => entry.CustomerId == customerId)
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(limit)
            .Select(entry => new
            {
                entry.Id,
                entry.OccurredAt,
                entry.Action,
                entry.ActorType,
                entry.ActorDisplay,
            })
            .ToArrayAsync(cancellationToken);

        return entries
            .Select(entry => new ActivityItem(
                entry.Id,
                entry.OccurredAt,
                Classify(entry.Action),
                entry.Action,
                entry.ActorDisplay ?? entry.ActorType.ToString()))
            .ToArray();
    }

    /// <summary>
    /// A store's measured activity, bucketed by hour.
    ///
    /// Bucketing happens in the database. Pulling a week of error rows back to
    /// count them in memory would make the store detail page the most expensive
    /// screen in the product.
    /// </summary>
    public async Task<StoreUsage?> ReadStoreUsageAsync(Guid storeId, int hours, CancellationToken cancellationToken)
    {
        var exists = await _context.Stores.AsNoTracking().AnyAsync(store => store.Id == storeId, cancellationToken);

        if (!exists)
        {
            return null;
        }

        var window = Math.Clamp(hours, 1, 168);
        var since = DateTimeOffset.UtcNow.AddHours(-window);

        var errorBuckets = await _context.StoreErrorEvents
            .AsNoTracking()
            .Where(error => error.StoreId == storeId && error.ReceivedAt >= since)
            .GroupBy(error => new { error.ReceivedAt.Year, error.ReceivedAt.Month, error.ReceivedAt.Day, error.ReceivedAt.Hour })
            .Select(group => new { group.Key.Year, group.Key.Month, group.Key.Day, group.Key.Hour, Count = group.Count() })
            .ToArrayAsync(cancellationToken);

        var logBuckets = await _context.StoreLogEntries
            .AsNoTracking()
            .Where(entry => entry.StoreId == storeId && entry.ReceivedAt >= since)
            .GroupBy(entry => new { entry.ReceivedAt.Year, entry.ReceivedAt.Month, entry.ReceivedAt.Day, entry.ReceivedAt.Hour })
            .Select(group => new { group.Key.Year, group.Key.Month, group.Key.Day, group.Key.Hour, Count = group.Count() })
            .ToArrayAsync(cancellationToken);

        var latencyBuckets = await _context.StoreHealthChecks
            .AsNoTracking()
            .Where(check => check.StoreId == storeId && check.CheckedAt >= since && check.ResponseTimeMs != null)
            .GroupBy(check => new { check.CheckedAt.Year, check.CheckedAt.Month, check.CheckedAt.Day, check.CheckedAt.Hour })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                group.Key.Day,
                group.Key.Hour,
                Average = group.Average(check => (double)check.ResponseTimeMs!),
            })
            .ToArrayAsync(cancellationToken);

        var errors = new int[window];
        var logs = new int[window];
        var latency = new int[window];

        var start = new DateTimeOffset(since.Year, since.Month, since.Day, since.Hour, 0, 0, TimeSpan.Zero);

        int? IndexOf(int year, int month, int day, int hour)
        {
            var slot = (int)(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero) - start).TotalHours;

            return slot >= 0 && slot < window ? slot : null;
        }

        foreach (var bucket in errorBuckets)
        {
            if (IndexOf(bucket.Year, bucket.Month, bucket.Day, bucket.Hour) is { } slot)
            {
                errors[slot] = bucket.Count;
            }
        }

        foreach (var bucket in logBuckets)
        {
            if (IndexOf(bucket.Year, bucket.Month, bucket.Day, bucket.Hour) is { } slot)
            {
                logs[slot] = bucket.Count;
            }
        }

        foreach (var bucket in latencyBuckets)
        {
            if (IndexOf(bucket.Year, bucket.Month, bucket.Day, bucket.Hour) is { } slot)
            {
                latency[slot] = (int)Math.Round(bucket.Average);
            }
        }

        return new StoreUsage(errors, logs, latency, window, errors.Sum(), logs.Sum());
    }

    private static string Translate(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "Healthy",
        HealthStatus.Degraded => "Degraded",
        HealthStatus.Unhealthy => "Offline",
        _ => "Unknown",
    };

    /// <summary>
    /// Turns an audit action into the tone the activity feed shows it in.
    /// Anything that reads as a failure is a warning, so a feed being skimmed
    /// still surfaces the entries worth stopping on.
    /// </summary>
    private static string Classify(string action) =>
        action.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
        action.Contains("revoke", StringComparison.OrdinalIgnoreCase) ||
        action.Contains("suspend", StringComparison.OrdinalIgnoreCase)
            ? "warning"
            : action.StartsWith("incident", StringComparison.OrdinalIgnoreCase) ||
              action.StartsWith("alert", StringComparison.OrdinalIgnoreCase)
                ? "event"
                : action.Contains("system", StringComparison.OrdinalIgnoreCase)
                    ? "system"
                    : "user";

    private static string Humanise(string key) => key switch
    {
        "npgsql" or "database" or "postgres" => "PostgreSQL",
        "redis" or "cache" => "Redis",
        _ => key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..],
    };
}
