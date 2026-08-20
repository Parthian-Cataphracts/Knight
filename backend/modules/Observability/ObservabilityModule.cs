using System.ComponentModel.DataAnnotations;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Observability;

/// <summary>
/// The rule keys this module raises, as constants, for the same reason
/// <c>AlertRules</c> exists: deduplication, routing and the dashboard's filters
/// all key on these strings, and a typo does not fail — it quietly opens a
/// second, parallel alert stream nobody is watching.
///
/// The delivery rules named in `TODO.md` phase 5 live here rather than beside
/// the delivery engine because the *conditions* they describe are differences
/// between what two modules believe, and neither module owns the comparison.
/// </summary>
public static class ObservabilityRules
{
    /// <summary>A group's rate has jumped well above its own recent baseline.</summary>
    public const string ErrorSpike = "errors.spike";

    /// <summary>A group somebody had resolved has started happening again.</summary>
    public const string ErrorRegression = "errors.regression";

    /// <summary>An installation job ended in failure.</summary>
    public const string FeatureInstallFailed = "feature.install.failed";

    /// <summary>A customer is entitled to a capability that is not installed, past the grace period.</summary>
    public const string FeatureEntitledNotInstalled = "feature.entitled_not_installed";

    /// <summary>The store reports a version other than the one KNIGHT installed.</summary>
    public const string FeatureDrift = "feature.drift";

    /// <summary>A job was claimed and never reported again.</summary>
    public const string JobStuck = "job.stuck";

    /// <summary>
    /// No successful backup has been reported for a store in longer than the
    /// configured window. The quiet failure: a backup job that stopped running
    /// says nothing at all, which is why only a timer can find it.
    /// </summary>
    public const string BackupOverdue = "backup.overdue";

    /// <summary>Every rule this module can raise, for the settings screen and for validation.</summary>
    public static readonly IReadOnlyCollection<string> All =
    [
        ErrorSpike,
        ErrorRegression,
        FeatureInstallFailed,
        FeatureEntitledNotInstalled,
        FeatureDrift,
        JobStuck,
        BackupOverdue,
    ];
}

/// <summary>
/// Bound from configuration (section "Observability").
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// How many full events are kept per error group. Samples are what make a
    /// group actionable and also the only part of it that grows, so the cap is
    /// the difference between a readable error screen and an unbounded table.
    /// </summary>
    [Range(1, 200)]
    public int MaxSamplesPerGroup { get; init; } = 20;

    /// <summary>How often the rule sweep runs.</summary>
    [Range(typeof(TimeSpan), "00:00:15", "01:00:00")]
    public TimeSpan EvaluationInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The window a spike is measured over.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "06:00:00")]
    public TimeSpan SpikeWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many occurrences inside the window before a spike is even considered.
    /// Without a floor, a group that went from one error to four would page
    /// somebody at 3am for a 300% increase over nothing.
    /// </summary>
    [Range(1, 10000)]
    public int SpikeMinimumCount { get; init; } = 20;

    /// <summary>
    /// How many times the window's rate must exceed the group's established rate
    /// to count as a spike.
    /// </summary>
    [Range(1.5, 100)]
    public double SpikeMultiplier { get; init; } = 5;

    /// <summary>
    /// How long an entitlement may go uninstalled before it is a problem.
    /// Installation is asynchronous by design, so anything shorter than this
    /// would alert on the system working normally.
    /// </summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan InstallationGrace { get; init; } = TimeSpan.FromHours(2);

    /// <summary>How long a claimed job may go unreported before it is presumed stuck.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "24:00:00")]
    public TimeSpan StuckJobThreshold { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a store may go without a successful backup before KNIGHT says
    /// so. Slightly more than a day by default, so a nightly backup that runs an
    /// hour late does not page anybody, and one that did not run at all does.
    /// </summary>
    public TimeSpan BackupInterval { get; init; } = TimeSpan.FromHours(26);

    /// <summary>How far back the sweep looks for job failures it has not alerted on yet.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan FailureLookback { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How often the notification dispatcher looks for work.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan DispatchInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How many deliveries one dispatch pass will attempt.</summary>
    [Range(1, 500)]
    public int DispatchBatchSize { get; init; } = 50;

    /// <summary>How many attempts a delivery gets before it is abandoned.</summary>
    [Range(1, 20)]
    public int MaxDeliveryAttempts { get; init; } = 5;

    /// <summary>The first retry delay; each subsequent one doubles up to <see cref="MaxRetryDelay"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(30);

    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Consecutive failures before a channel is switched off. A webhook that has
    /// rejected everything all week is not going to accept the next one, and
    /// pretending otherwise hides that nobody has been notified of anything.
    /// </summary>
    [Range(2, 100)]
    public int ChannelFailureThreshold { get; init; } = 10;

    /// <summary>
    /// How long the same rule and subject is suppressed after a notification for
    /// it. Deduplication at the delivery layer, on top of alert deduplication:
    /// an alert that is re-observed must not re-page.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan NotificationCooldown { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a webhook send may take before it is treated as failed.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan WebhookTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Severities at or above which a rule opens an incident automatically.
    /// Critical only, by default: an incident is a claim that people are
    /// responding, and opening one for every warning devalues the word.
    /// </summary>
    public bool OpenIncidentsAutomatically { get; init; } = true;
}

public static class ObservabilityModuleExtensions
{
    public static IServiceCollection AddObservabilityModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection(ObservabilityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<ErrorService>();
        services.AddScoped<IErrorService>(provider => provider.GetRequiredService<ErrorService>());

        // The same object satisfies the module's own read API and the
        // application-layer port ingestion writes through. One implementation,
        // because grouping on the way in and grouping on the way out must not be
        // able to disagree about what a fingerprint is.
        services.AddScoped<IErrorGrouping>(provider => provider.GetRequiredService<ErrorService>());

        services.AddScoped<IIncidentService, IncidentService>();

        services.AddScoped<NotificationService>();
        services.AddScoped<INotificationService>(provider => provider.GetRequiredService<NotificationService>());

        // Alerts raised anywhere in the control plane arrive here, where the
        // decision to open an incident and to tell somebody is made.
        services.AddScoped<IAlertEventPublisher>(provider => provider.GetRequiredService<NotificationService>());

        services.AddScoped<IObservabilityRuleEvaluator, ObservabilityRuleEvaluator>();

        return services;
    }
}
