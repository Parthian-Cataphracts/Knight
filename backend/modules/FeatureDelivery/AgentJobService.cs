using System.Text.Json;
using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Observability;
using Knight.Application.Abstractions.Security;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Options;

namespace FeatureDelivery;

/// <summary>
/// The agent side of delivery.
///
/// Every method takes the store id the caller authenticated as and checks the
/// job against it. That check is not redundant with the persistence filter: an
/// agent authenticates as a store, not as a customer, and "this job belongs to
/// some store of the same customer" is not good enough — a customer with two
/// stores must not have one of them execute the other's install
/// (docs/authorization.md).
/// </summary>
internal sealed class AgentJobService : IAgentJobService
{
    private readonly IFeatureInstallationJobRepository _jobs;
    private readonly IFeatureInstallationRepository _installations;
    private readonly IFeatureConfigurationRepository _configurations;
    private readonly IFeatureVersionReader _versions;
    private readonly IFeatureArtifactStore _artifacts;
    private readonly ISecretProtector _secrets;
    private readonly IAuditTrail _audit;
    private readonly IKnightMetrics _metrics;
    private readonly IDateTimeProvider _clock;
    private readonly FeatureDeliveryOptions _options;

    public AgentJobService(
        IFeatureInstallationJobRepository jobs,
        IFeatureInstallationRepository installations,
        IFeatureConfigurationRepository configurations,
        IFeatureVersionReader versions,
        IFeatureArtifactStore artifacts,
        ISecretProtector secrets,
        IAuditTrail audit,
        IKnightMetrics metrics,
        IDateTimeProvider clock,
        IOptions<FeatureDeliveryOptions> options)
    {
        _jobs = jobs;
        _installations = installations;
        _configurations = configurations;
        _versions = versions;
        _artifacts = artifacts;
        _secrets = secrets;
        _audit = audit;
        _metrics = metrics;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<AgentJobAssignment?> ClaimNextAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var job = await _jobs.FindNextForStoreAsync(storeId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var now = _clock.UtcNow;
        job.Claim(now, _options.JobClaimTimeout);

        // Only install and upgrade jobs put the installation into Pending when
        // they were queued, so only they have work to begin. A disable or
        // uninstall acts on a feature that is installed and staying that way
        // until the job reports, and telling it to begin work would be an
        // illegal transition the aggregate would rightly refuse.
        if (job.Type is JobType.Install or JobType.Upgrade)
        {
            var installation = await _installations.GetByIdAsync(job.InstallationId, cancellationToken);
            installation?.BeginWork(job.Id, now);
        }

        await _jobs.SaveChangesAsync(cancellationToken);
        await _installations.SaveChangesAsync(cancellationToken);

        return await DescribeAsync(job, cancellationToken);
    }

    public async Task ReportStepAsync(Guid storeId, Guid jobId, StepReport report, CancellationToken cancellationToken)
    {
        var job = await RequireJobAsync(storeId, jobId, cancellationToken);

        if (!Enum.TryParse<StepStatus>(report.Status, ignoreCase: true, out var status))
        {
            throw new ValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["status"] = [$"'{report.Status}' is not a step status."],
            });
        }

        var created = job.ReportStep(
            report.Step,
            status,
            _clock.UtcNow,
            report.Output,
            report.ErrorCode,
            report.DurationMilliseconds);

        if (created is not null)
        {
            _jobs.RegisterNewStep(created);
        }

        await _jobs.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteAsync(Guid storeId, Guid jobId, JobCompletionReport report, CancellationToken cancellationToken)
    {
        var job = await RequireJobAsync(storeId, jobId, cancellationToken);
        var now = _clock.UtcNow;

        var installation = await _installations.GetByIdAsync(job.InstallationId, cancellationToken)
            ?? throw new NotFoundException("The installation this job belongs to no longer exists.");

        if (report.Succeeded)
        {
            job.Succeed(now);
            ApplySuccess(job, installation, report, now);
        }
        else
        {
            var outcome = ParseRollbackOutcome(report.RollbackOutcome);
            var code = string.IsNullOrWhiteSpace(report.FailureCode) ? "job.failed" : report.FailureCode;
            var message = string.IsNullOrWhiteSpace(report.FailureMessage) ? "The agent reported a failure." : report.FailureMessage;

            job.Fail(code, message, outcome, now);
            installation.MarkFailed(job.Id, code, message, outcome, now);
        }

        if (job.Type is JobType.ApplyConfiguration && report.Succeeded)
        {
            var configuration = await _configurations.FindAsync(job.StoreId, job.FeatureId, cancellationToken);
            configuration?.RecordApplied(configuration.Version, now);
            await _configurations.SaveChangesAsync(cancellationToken);
        }

        await _jobs.SaveChangesAsync(cancellationToken);
        await _installations.SaveChangesAsync(cancellationToken);

        RecordMetrics(job, report, now);

        await _audit.RecordAsync(
            report.Succeeded ? "feature.job.succeeded" : "feature.job.failed",
            "FeatureInstallationJob",
            job.Id.ToString(),
            job.CustomerId,
            cancellationToken,
            newValue: new
            {
                job.FeatureSlug,
                Type = job.Type.ToString(),
                job.TargetVersion,
                report.FailureCode,
                RollbackOutcome = job.RollbackOutcome.ToString(),
            });
    }

    /// <summary>
    /// Records what the job cost and how it ended.
    ///
    /// Duration is measured from when the agent claimed it rather than from when
    /// it was queued, because queue time says how busy the fleet is and execution
    /// time says how slow the work is — and an operator chasing a slow install
    /// needs the second, not their sum.
    /// </summary>
    private void RecordMetrics(FeatureInstallationJob job, JobCompletionReport report, DateTimeOffset now)
    {
        var started = job.ClaimedAt ?? job.QueuedAt;

        _metrics.JobCompleted(
            job.Type.ToString(),
            report.Succeeded ? "succeeded" : "failed",
            Math.Max((now - started).TotalSeconds, 0));

        if (report.Succeeded)
        {
            return;
        }

        // Which step failed is read from the steps the agent reported rather
        // than from the completion payload, which does not carry it — and the
        // step is the dimension an operator groups by when asking "what keeps
        // breaking", so guessing it would make the metric misleading.
        var failedStep = job.Steps
            .Where(step => step.Status is StepStatus.Failed)
            .Select(step => step.Name)
            .LastOrDefault() ?? "unknown";

        _metrics.JobStepFailed(
            job.Type.ToString(),
            failedStep,
            string.IsNullOrWhiteSpace(report.FailureCode) ? "job.failed" : report.FailureCode);

        if (job.RollbackOutcome is not RollbackOutcome.NotAttempted)
        {
            // The share of rollbacks needing a human is the number that says
            // whether failures are self-healing or whether somebody is paged.
            _metrics.RollbackCompleted(job.RollbackOutcome.ToString());
        }
    }

    /// <summary>
    /// Applies a successful outcome to the installation.
    ///
    /// Which transition a success means depends on what the job was for, and
    /// getting that wrong is how a disabled feature comes back as installed.
    /// </summary>
    private static void ApplySuccess(
        FeatureInstallationJob job,
        FeatureInstallation installation,
        JobCompletionReport report,
        DateTimeOffset now)
    {
        switch (job.Type)
        {
            case JobType.Install or JobType.Upgrade or JobType.Rollback:
                installation.MarkInstalled(job.Id, now);
                break;

            case JobType.Enable:
                installation.Enable(now);
                break;

            case JobType.Disable:
                installation.Disable(now);
                break;

            case JobType.Uninstall:
                installation.MarkUninstalled(job.Id, DefaultRetentionDays, now);
                break;

            case JobType.ApplyConfiguration:
                // Configuration does not move the installation's state; it only
                // proves the store is still healthy afterwards.
                break;
        }

        if (!string.IsNullOrWhiteSpace(report.Health) &&
            Enum.TryParse<FeatureHealth>(report.Health, ignoreCase: true, out var health))
        {
            installation.RecordHealth(health, now);
        }
    }

    public async Task<int> SweepExpiredClaimsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var expired = await _jobs.ListExpiredClaimsAsync(now, cancellationToken);
        var swept = 0;

        foreach (var job in expired)
        {
            var requeued = job.TimeOut(now);
            swept++;

            if (requeued)
            {
                // The installation goes back to Pending so the state matches the
                // queue: an installation stuck in Installing with no running job
                // is a store nobody can act on.
                var installation = await _installations.GetByIdAsync(job.InstallationId, cancellationToken);
                if (installation is not null && installation.CurrentJobId == job.Id)
                {
                    installation.RecordBlockingReason(
                        $"The agent stopped reporting; attempt {job.AttemptCount} of {job.MaxAttempts} will be retried.",
                        now);
                }

                continue;
            }

            var failed = await _installations.GetByIdAsync(job.InstallationId, cancellationToken);
            if (failed is not null && failed.CurrentJobId == job.Id)
            {
                failed.MarkFailed(job.Id, job.FailureCode!, job.FailureMessage!, RollbackOutcome.NotAttempted, now);
            }
        }

        if (swept > 0)
        {
            await _jobs.SaveChangesAsync(cancellationToken);
            await _installations.SaveChangesAsync(cancellationToken);
        }

        return swept;
    }

    /// <summary>
    /// Builds the payload the agent receives, including a freshly minted download
    /// URL and the decrypted configuration.
    /// </summary>
    private async Task<AgentJobAssignment> DescribeAsync(FeatureInstallationJob job, CancellationToken cancellationToken)
    {
        AgentArtifact? artifact = null;
        AgentMigrationPolicy? migrations = null;

        if (job.TargetVersionId is { } versionId)
        {
            var version = await _versions.GetForDeliveryAsync(versionId, cancellationToken);
            if (version is not null)
            {
                var expiresAt = _clock.UtcNow.Add(_options.ArtifactUrlLifetime);
                var url = await _artifacts.CreateDownloadUrlAsync(
                    version.PackageReference,
                    _options.ArtifactUrlLifetime,
                    cancellationToken);

                artifact = new AgentArtifact(
                    version.PackageReference,
                    version.Digest,
                    version.SizeBytes,
                    version.Signature,
                    version.SigningKeyId,
                    url,
                    expiresAt);

                migrations = new AgentMigrationPolicy(
                    version.MigrationsRequired,
                    version.MigrationsReversible,
                    version.RequiresMaintenanceWindow);
            }
        }

        AgentConfiguration? configuration = null;
        var stored = await _configurations.FindAsync(job.StoreId, job.FeatureId, cancellationToken);
        if (stored is not null)
        {
            var secrets = stored.EncryptedSecretsJson is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(_secrets.Unprotect(stored.EncryptedSecretsJson))
                  ?? new Dictionary<string, string>(StringComparer.Ordinal);

            configuration = new AgentConfiguration(stored.Version, stored.ValuesJson, secrets);
        }

        return new AgentJobAssignment(
            job.Id,
            job.Type.ToString(),
            job.FeatureSlug,
            job.TargetVersion,
            job.CorrelationId,
            job.TraceParent,
            JobPipeline.StepsFor(job.Type),
            job.NextStep(),
            artifact,
            configuration,
            migrations,
            job.ClaimExpiresAt ?? _clock.UtcNow.Add(_options.JobClaimTimeout));
    }

    private async Task<FeatureInstallationJob> RequireJobAsync(Guid storeId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetByIdAsync(jobId, cancellationToken)
            ?? throw new NotFoundException($"Job '{jobId}' was not found.");

        // The store the agent authenticated as, not the customer. A customer with
        // two stores must not have one execute the other's install.
        if (job.StoreId != storeId)
        {
            throw new NotFoundException($"Job '{jobId}' was not found.");
        }

        return job;
    }

    private static RollbackOutcome ParseRollbackOutcome(string? value) =>
        Enum.TryParse<RollbackOutcome>(value, ignoreCase: true, out var outcome)
            ? outcome
            : RollbackOutcome.NotAttempted;

    /// <summary>
    /// The retention window used when a manifest does not say. Matches the
    /// documented default of docs/feature-delivery.md §11 — a month is long
    /// enough that a customer who renews loses nothing.
    /// </summary>
    private const int DefaultRetentionDays = 30;
}

/// <summary>
/// The few facts about a published version that delivery needs to hand to an
/// agent. A reader rather than a module reference: delivery must not depend on
/// the registry, and it does not need the aggregate — only what to fetch, how to
/// verify it, and whether its migrations can be undone.
/// </summary>
public interface IFeatureVersionReader
{
    Task<DeliverableVersion?> GetForDeliveryAsync(Guid versionId, CancellationToken cancellationToken);
}

public sealed record DeliverableVersion(
    Guid VersionId,
    string Slug,
    string Version,
    string PackageReference,
    string Digest,
    long SizeBytes,
    string Signature,
    string SigningKeyId,
    bool MigrationsRequired,
    bool MigrationsReversible,
    bool RequiresMaintenanceWindow,
    int DataRetentionDays);
