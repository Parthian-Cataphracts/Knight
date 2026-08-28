using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent;

/// <summary>What one step did, or why it did not.</summary>
public sealed record StepOutcome(string Step, string Status, string Detail, string? Code, long DurationMilliseconds);

/// <summary>What a whole job did.</summary>
public sealed record JobOutcome
{
    public required bool Succeeded { get; init; }

    public string? FailedStep { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    public string? InstalledVersion { get; init; }

    public IReadOnlyList<StepOutcome> Steps { get; init; } = [];
}

/// <summary>
/// Running one job's steps in order, and reporting what happened.
///
/// The order comes from KNIGHT — the job carries its own step list — rather than
/// being decided here. Two properties this is built around, both learnt on the
/// other reference stores:
///
/// <list type="bullet">
/// <item><b>A step that fails stops the job</b>, and the failure carries the
/// step's own code, because "install failed" and "the signature was wrong" need
/// different people woken up.</item>
/// <item><b>The outcome is recorded whether it succeeded or not.</b> A job that
/// failed silently is a Feature a merchant has paid for and does not have.</item>
/// </list>
///
/// An unknown step is refused rather than skipped. Skipping one would let a
/// KNIGHT that had learnt a new verb believe this store had performed it — which
/// is exactly how the node store came to be missing three verbs for three
/// phases without anybody noticing.
/// </summary>
public sealed class JobRunner(
    KnightClient client,
    IOptions<KnightOptions> options,
    ILogger<JobRunner> logger)
{
    private readonly KnightOptions _options = options.Value;

    /// <summary>A step, and what this store does for it.</summary>
    private delegate Task<string> Step(JobContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Everything a step needs, passed rather than reached for.
    ///
    /// The client and the options live here rather than in a static the step
    /// table closes over. A library hosted inside somebody else's application
    /// must not hold process-wide mutable state: two stores in one process, or a
    /// test host building a second runner, would have silently reconfigured the
    /// first.
    /// </summary>
    private sealed class JobContext
    {
        public required AgentJob Job { get; init; }

        public required FeatureRegistry Registry { get; init; }

        public required KnightClient Client { get; init; }

        public required KnightOptions Options { get; init; }

        public byte[]? Bytes { get; set; }

        public string Slug => Job.FeatureSlug;
    }

    public async Task<JobOutcome> RunAsync(
        AgentJob job,
        Func<StepOutcome, CancellationToken, Task>? onStep,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.FeatureRoot);
        Directory.CreateDirectory(_options.Workspace);

        var context = new JobContext
        {
            Job = job,
            Registry = new FeatureRegistry(_options.FeatureRoot),
            Client = client,
            Options = _options,
        };
        var done = new List<StepOutcome>();

        var steps = job.Steps.Count > 0
            ? job.Steps
            : new[] { "preflight", "fetch", "verify", "install", "configure", "enable", "healthcheck" };

        foreach (var name in steps)
        {
            if (!Vocabulary.TryGetValue(name, out var step))
            {
                var unknown = new StepOutcome(name, "Failed", $"This store does not know how to '{name}'.", "step.unknown", 0);
                await ReportAsync(onStep, unknown, cancellationToken);

                return new JobOutcome
                {
                    Succeeded = false,
                    FailedStep = name,
                    Code = unknown.Code,
                    Detail = unknown.Detail,
                    Steps = done,
                };
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var detail = await step(context, cancellationToken);
                stopwatch.Stop();

                var outcome = new StepOutcome(name, "Succeeded", detail, null, stopwatch.ElapsedMilliseconds);
                done.Add(outcome);
                await ReportAsync(onStep, outcome, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                stopwatch.Stop();

                var code = exception is StepFailedException failure ? failure.Code : "step.failed";
                var outcome = new StepOutcome(name, "Failed", exception.Message, code, stopwatch.ElapsedMilliseconds);
                await ReportAsync(onStep, outcome, cancellationToken);

                logger.LogError(exception, "Job {JobId} failed at {Step}.", job.JobId, name);

                return new JobOutcome
                {
                    Succeeded = false,
                    FailedStep = name,
                    Code = code,
                    Detail = exception.Message,
                    Steps = done,
                };
            }
        }

        return new JobOutcome { Succeeded = true, InstalledVersion = job.TargetVersion, Steps = done };
    }

    /// <summary>
    /// Telling the caller about a step must never stop the job.
    ///
    /// A store that abandoned an install because a progress report did not go
    /// through would be a store where a flaky network uninstalls Features. The
    /// outcome is reported again at the end, so nothing is lost.
    /// </summary>
    private async Task ReportAsync(Func<StepOutcome, CancellationToken, Task>? onStep, StepOutcome outcome, CancellationToken cancellationToken)
    {
        if (onStep is null)
        {
            return;
        }

        try
        {
            await onStep(outcome, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not report step {Step} to KNIGHT.", outcome.Step);
        }
    }

    // --- The vocabulary ----------------------------------------------------
    //
    // KNIGHT's verbs, not .NET's. Every one of the eleven a job can name is
    // here, because a store that knows ten of them refuses the eleventh and the
    // job that names it fails for a reason nobody can act on.

    private static readonly Dictionary<string, Step> Vocabulary = new(StringComparer.Ordinal)
    {
        ["preflight"] = Preflight,
        ["fetch"] = Fetch,
        ["verify"] = Verify,
        ["backup"] = Backup,
        ["install"] = Install,
        ["create-extensions"] = CreateExtensions,
        ["migrate"] = Migrate,
        ["configure"] = Configure,
        ["enable"] = Enable,
        ["disable"] = Disable,
        ["reload"] = Reload,
        ["healthcheck"] = HealthCheck,
        ["restore-package"] = RestorePackage,
        ["reverse-migrate"] = ReverseMigrate,
        ["remove-package"] = RemovePackage,
    };

    private static Task<string> Preflight(JobContext context, CancellationToken cancellationToken)
    {
        // An external Feature has no runtime to match: this store loads none of
        // its code, so there is no package built for anything. What matters for
        // it is whether this store publishes the events it wants, and that is
        // checked at install against the signed document rather than here
        // against the job (adr/0033).
        if (context.Job.IsExternalService)
        {
            return Task.FromResult($"{context.Slug} is a service; this store will register it and run none of it");
        }

        var runtime = context.Job.Runtime;

        if (runtime is null)
        {
            throw new StepFailedException("preflight.no_runtime", "The job does not say how the Feature attaches to this store.");
        }

        // Absent means django everywhere else in the contract, so a .NET store
        // must refuse it rather than assume it was meant for them.
        if (!string.Equals(runtime.Name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new StepFailedException(
                "preflight.wrong_runtime",
                $"This store runs dotnet and the job is for '{runtime.Name}'.");
        }

        if (string.IsNullOrWhiteSpace(runtime.Module))
        {
            throw new StepFailedException("preflight.no_module", "The job does not name an assembly to load.");
        }

        return Task.FromResult($"{context.Slug} is a dotnet Feature and this store runs dotnet");
    }

    private static async Task<string> Fetch(JobContext context, CancellationToken cancellationToken)
    {
        var artifact = context.Job.Artifact
            ?? throw new StepFailedException("fetch.no_artifact", "The job names no artifact to fetch.");

        if (!Uri.TryCreate(artifact.DownloadUrl, UriKind.Absolute, out var url))
        {
            throw new StepFailedException("fetch.bad_url", $"'{artifact.DownloadUrl}' is not a URL this store can fetch.");
        }

        context.Bytes = url.IsFile
            ? await File.ReadAllBytesAsync(url.LocalPath, cancellationToken)
            : await context.Client.DownloadAsync(url, artifact.SizeBytes, cancellationToken);

        return $"fetched {context.Bytes.Length} byte(s)";
    }

    private static Task<string> Verify(JobContext context, CancellationToken cancellationToken)
    {
        var artifact = context.Job.Artifact
            ?? throw new StepFailedException("verify.no_artifact", "There is nothing to verify.");

        var bytes = context.Bytes
            ?? throw new StepFailedException("verify.nothing_fetched", "Nothing was fetched to verify.");

        var digest = ArtifactVerifier.VerifyDigest(bytes, artifact.Digest);
        ArtifactVerifier.VerifySignature(digest, artifact.Signature, artifact.SigningKeyId, context.Options.SigningKeys);

        return Task.FromResult($"verified {digest[..12]} signed by {artifact.SigningKeyId}");
    }

    private static async Task<string> Backup(JobContext context, CancellationToken cancellationToken)
    {
        if (context.Job.IsExternalService)
        {
            // The registration this job is about to replace, kept beside the
            // feature root rather than in the job's workspace: a workspace is
            // deleted when its job finishes and a rollback is a different job.
            var current = await context.Registry.FindAsync(context.Slug, cancellationToken);

            if (current is null)
            {
                return "nothing registered yet; no backup needed";
            }

            await File.WriteAllTextAsync(
                ExternalBackupPath(context),
                System.Text.Json.JsonSerializer.Serialize(current, ExternalJson),
                cancellationToken);

            return $"kept the registration of {current.Slug} {current.Version}";
        }

        return BackupPackage(context);
    }

    private static string BackupPackage(JobContext context)
    {
        var target = Path.Combine(context.Options.FeatureRoot, context.Slug);

        if (!Directory.Exists(target))
        {
            return "nothing installed to back up";
        }

        var previous = target + ".previous";

        // Kept beside the Feature rather than in the job's workspace. A rollback
        // is a *different* job, and a workspace is deleted when its job
        // finishes — which is how backups came to be deleted before anything
        // could restore them.
        if (Directory.Exists(previous))
        {
            Directory.Delete(previous, recursive: true);
        }

        CopyDirectory(target, previous);

        return $"kept the current install at {previous}";
    }

    private static Task<string> Install(JobContext context, CancellationToken cancellationToken)
    {
        if (context.Job.IsExternalService)
        {
            return InstallExternalAsync(context, cancellationToken);
        }

        var bytes = context.Bytes
            ?? throw new StepFailedException("install.nothing_verified", "Nothing was fetched to install.");

        var target = Path.Combine(context.Options.FeatureRoot, context.Slug);

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var written = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            // A delivered artifact reaching out of the directory it was given is
            // the oldest bug in unpacking. Resolved and checked rather than
            // trusted, on every entry.
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));

            if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new StepFailedException(
                    "install.path_traversal",
                    $"The artifact contains an entry that escapes its directory: '{entry.FullName}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            written++;
        }

        return Task.FromResult($"installed {written} file(s) into {target}");
    }

    private static Task<string> CreateExtensions(JobContext context, CancellationToken cancellationToken)
    {
        var extensions = context.Job.Migrations?.Extensions ?? [];

        if (extensions.Count == 0)
        {
            return Task.FromResult("no extensions declared");
        }

        // This library does not own the store's database and must not open one.
        // A Feature that needs an extension needs a decision from whoever runs
        // the store, and saying so is better than a migration that fails later
        // for a reason further from the cause.
        throw new StepFailedException(
            "extensions.unsupported",
            $"{context.Slug} requires the database extension(s) {string.Join(", ", extensions)}; " +
            "create them on the store's database before installing it.");
    }

    private static async Task<string> Migrate(JobContext context, CancellationToken cancellationToken)
    {
        if (context.Job.IsExternalService)
        {
            // Nothing in this store's database ever heard of this Feature. It
            // is not in the external install pipeline at all, and this branch
            // exists so that a job which names it anyway says something true
            // rather than recording a schema state for a schema that does not
            // exist.
            return $"{context.Slug} has no schema in this store; nothing to migrate";
        }

        var runtime = context.Job.Runtime!;
        var version = context.Job.TargetVersion ?? "unknown";

        // Recorded rather than run. A .NET store's migrations belong to the
        // store's own EF context and its own startup, and a delivered assembly
        // that opened the store's database and applied its own migrations would
        // be a Feature with more authority than the application hosting it.
        //
        // What KNIGHT's contract requires is that the store know what state the
        // Feature's schema is in under the declared namespace, and that is what
        // is recorded. A store that runs migrations here writes them against
        // this same namespace.
        await context.Registry.RecordMigrationAsync(runtime.Namespace, version, cancellationToken);

        return $"{runtime.Namespace} recorded at {version}";
    }

    private static async Task<string> Configure(JobContext context, CancellationToken cancellationToken)
    {
        var configuration = context.Job.Configuration;

        if (configuration is null)
        {
            return "no configuration to write";
        }

        // No package directory for an external Feature: creating one would leave
        // somewhere made for code that does not exist, which the next person to
        // look would read as a half-finished install.
        if (!context.Job.IsExternalService)
        {
            Directory.CreateDirectory(Path.Combine(context.Options.FeatureRoot, context.Slug));
        }

        Directory.CreateDirectory(context.Options.FeatureRoot);

        // Beside the Feature, not inside it. Inside would be overwritten by the
        // next install, which is how a node Feature came to read a config file
        // that the upgrade had just deleted.
        var path = Path.Combine(context.Options.FeatureRoot, $"{context.Slug}.config.json");
        await File.WriteAllTextAsync(path, configuration.ValuesJson, cancellationToken);

        var existing = await context.Registry.FindAsync(context.Slug, cancellationToken);

        if (existing is not null)
        {
            await context.Registry.RecordAsync(existing with { ConfigVersion = configuration.Version }, cancellationToken);
        }

        return $"configuration v{configuration.Version} written to {path}";
    }

    private static async Task<string> Enable(JobContext context, CancellationToken cancellationToken)
    {
        if (context.Job.IsExternalService)
        {
            // Everything but the flag was written by `install`. An external
            // Feature's entry has no module and no mount, and writing plausible
            // values into those would have the store try to load a package that
            // was never delivered.
            await context.Registry.SetEnabledAsync(context.Slug, true, cancellationToken);

            return $"{context.Slug} enabled";
        }

        await Record(context, enabled: true, cancellationToken);

        return $"{context.Slug} enabled";
    }

    private static async Task<string> Disable(JobContext context, CancellationToken cancellationToken)
    {
        await context.Registry.SetEnabledAsync(context.Slug, false, cancellationToken);

        return $"{context.Slug} disabled; its code and data are untouched";
    }

    private static Task<string> Reload(JobContext context, CancellationToken cancellationToken) =>
        // Reported rather than performed, and reported truthfully. An assembly
        // already loaded into this process stays loaded, so the Feature is on
        // disk and registered but is not being served until the store restarts.
        // Saying "reloaded" would be the store telling KNIGHT a lie that shows
        // up as a 404 a merchant reports.
        Task.FromResult($"{context.Slug} is installed; this store serves it after a restart");

    private static async Task<string> HealthCheck(JobContext context, CancellationToken cancellationToken)
    {
        var installed = await context.Registry.FindAsync(context.Slug, cancellationToken);

        if (installed is null)
        {
            throw new StepFailedException("healthcheck.not_installed", $"{context.Slug} is not in this store's registry.");
        }

        if (context.Job.IsExternalService)
        {
            return HealthCheckExternal(context, installed);
        }

        var assembly = Path.Combine(context.Options.FeatureRoot, context.Slug, installed.Module + ".dll");

        if (!File.Exists(assembly))
        {
            throw new StepFailedException(
                "healthcheck.missing_assembly",
                $"The registry says {context.Slug} is installed and {assembly} is not there.");
        }

        return $"{context.Slug} {installed.Version} is present and registered";
    }

    private static Task<string> RestorePackage(JobContext context, CancellationToken cancellationToken)
    {
        if (context.Job.IsExternalService)
        {
            return RestoreExternalAsync(context, cancellationToken);
        }

        var target = Path.Combine(context.Options.FeatureRoot, context.Slug);
        var previous = target + ".previous";

        if (!Directory.Exists(previous))
        {
            throw new StepFailedException(
                "rollback.no_backup",
                $"There is no kept copy of {context.Slug} to restore. Nothing has been changed.");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.Move(previous, target);

        return Task.FromResult($"restored the previous {context.Slug}");
    }

    private static async Task<string> ReverseMigrate(JobContext context, CancellationToken cancellationToken)
    {
        var runtime = context.Job.Runtime!;
        var target = context.Job.TargetVersion ?? "zero";

        await context.Registry.RecordMigrationAsync(runtime.Namespace, target, cancellationToken);

        return $"{runtime.Namespace} recorded back at {target}";
    }

    private static async Task<string> RemovePackage(JobContext context, CancellationToken cancellationToken)
    {
        if (context.Job.IsExternalService)
        {
            // There is no code to delete. What "remove" means here is the
            // registration going away, which is what stops the store forwarding
            // events and proxying routes to a Feature nobody is entitled to any
            // more. The Feature's own service and its own data are the author's,
            // and this store never had either.
            await context.Registry.RemoveAsync(context.Slug, cancellationToken);

            return $"unregistered {context.Slug}; its service keeps its own data";
        }

        var target = Path.Combine(context.Options.FeatureRoot, context.Slug);

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        await context.Registry.RemoveAsync(context.Slug, cancellationToken);

        return $"{context.Slug} removed";
    }

    private static readonly System.Text.Json.JsonSerializerOptions ExternalJson =
        new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Registers a Feature's events, routes and screens.
    ///
    /// No archive is unpacked, no directory is created and no database is
    /// opened. What "install" means for this architecture is exactly this
    /// registration, which is why it reuses the same verb rather than inventing
    /// one: a store that met a new verb would refuse the whole job.
    /// </summary>
    private static async Task<string> InstallExternalAsync(JobContext context, CancellationToken cancellationToken)
    {
        var bytes = context.Bytes
            ?? throw new StepFailedException("install.nothing_verified", "Nothing was fetched to install.");

        var contract = ExternalContract.Read(bytes, context.Slug);

        await context.Registry.RecordAsync(
            new InstalledFeature
            {
                Slug = context.Slug,
                Version = context.Job.TargetVersion ?? "unknown",
                Namespace = context.Job.Runtime?.Namespace ?? context.Slug.Replace('-', '_'),
                Module = string.Empty,
                MountType = null,
                MountPrefix = null,
                HealthCheck = null,
                Digest = context.Job.Artifact?.Digest,
                Enabled = false,
                InstalledAt = DateTimeOffset.UtcNow,
                ConfigVersion = context.Job.Configuration?.Version ?? 0,
                Contract = contract,
            },
            cancellationToken);

        return $"registered {contract.Webhooks.Count} webhook(s), {contract.ApiProxies.Count} proxy route(s) " +
               $"and {contract.UiMounts.Count} screen(s) for {context.Slug}";
    }

    /// <summary>
    /// Confirms the registration is complete and this store can act on it.
    ///
    /// Deliberately does not call the service. A service that is up at install
    /// time and down an hour later is the normal case, so gating the install on
    /// it would fail deliveries for something delivery cannot fix — and "is this
    /// Feature's service reachable" belongs in the store's own continuous health
    /// check, because it has to be asked repeatedly rather than once.
    /// </summary>
    private static string HealthCheckExternal(JobContext context, InstalledFeature installed)
    {
        if (!installed.IsExternalService)
        {
            throw new StepFailedException(
                "healthcheck.not_external",
                $"{context.Slug} is registered, and not as an external service.");
        }

        var contract = installed.Contract!;

        if (string.IsNullOrWhiteSpace(contract.Service?.BaseUrl))
        {
            throw new StepFailedException("healthcheck.no_service", $"{context.Slug} is registered with no service URL.");
        }

        var counts = (contract.Webhooks.Count, contract.ApiProxies.Count, contract.UiMounts.Count);

        if (counts is (0, 0, 0))
        {
            throw new StepFailedException(
                "healthcheck.registers_nothing",
                $"{context.Slug} is registered and subscribes to nothing, proxies nothing and shows nothing.");
        }

        return $"{context.Slug} {installed.Version} registered: {counts.Item1} webhook(s), " +
               $"{counts.Item2} route(s), {counts.Item3} screen(s)";
    }

    /// <summary>Where the kept registration lives. Beside the feature root, not in a workspace.</summary>
    private static string ExternalBackupPath(JobContext context) =>
        Path.Combine(context.Options.FeatureRoot, $"{context.Slug}.previous.json");

    /// <summary>
    /// Puts the kept registration back.
    ///
    /// From the local copy rather than by fetching the older version, for the
    /// same reason the in-process rollback restores rather than re-downloads: a
    /// rollback job names the version it is rolling *to* and carries the
    /// artifact of the one it is rolling *from*, so a store that fetched here
    /// would reinstall the version it was trying to leave.
    /// </summary>
    private static async Task<string> RestoreExternalAsync(JobContext context, CancellationToken cancellationToken)
    {
        var kept = ExternalBackupPath(context);

        if (!File.Exists(kept))
        {
            throw new StepFailedException(
                "rollback.no_backup",
                $"There is no kept registration of a previous {context.Slug} to restore, so nothing was rolled back.");
        }

        InstalledFeature? entry;

        try
        {
            entry = System.Text.Json.JsonSerializer.Deserialize<InstalledFeature>(
                await File.ReadAllTextAsync(kept, cancellationToken),
                ExternalJson);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new StepFailedException(
                "rollback.unreadable_backup",
                $"The kept registration could not be read: {exception.Message}");
        }

        if (entry is null)
        {
            throw new StepFailedException("rollback.unreadable_backup", "The kept registration is empty.");
        }

        // Restored switched off; `enable` later in the pipeline turns it back
        // on. A Feature that started serving the instant its registration came
        // back would be serving before the configuration for that version had
        // been written.
        await context.Registry.RecordAsync(entry with { Enabled = false }, cancellationToken);

        return $"restored the registration of {entry.Slug} {entry.Version}";
    }

    private static async Task Record(JobContext context, bool enabled, CancellationToken cancellationToken)
    {
        var runtime = context.Job.Runtime!;

        await context.Registry.RecordAsync(
            new InstalledFeature
            {
                Slug = context.Slug,
                Version = context.Job.TargetVersion ?? "unknown",
                Namespace = runtime.Namespace,
                Module = runtime.Module,
                MountType = runtime.MountExport,
                MountPrefix = runtime.MountPrefix,
                HealthCheck = context.Job.HealthCheck,
                Digest = context.Job.Artifact?.Digest,
                Enabled = enabled,
                InstalledAt = DateTimeOffset.UtcNow,
                ConfigVersion = context.Job.Configuration?.Version ?? 0,
            },
            cancellationToken);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
