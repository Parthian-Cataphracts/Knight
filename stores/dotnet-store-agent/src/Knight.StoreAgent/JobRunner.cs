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

    private static Task<string> Backup(JobContext context, CancellationToken cancellationToken)
    {
        var target = Path.Combine(context.Options.FeatureRoot, context.Slug);

        if (!Directory.Exists(target))
        {
            return Task.FromResult("nothing installed to back up");
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

        return Task.FromResult($"kept the current install at {previous}");
    }

    private static Task<string> Install(JobContext context, CancellationToken cancellationToken)
    {
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

        var directory = Path.Combine(context.Options.FeatureRoot, context.Slug);
        Directory.CreateDirectory(directory);

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
        var target = Path.Combine(context.Options.FeatureRoot, context.Slug);

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        await context.Registry.RemoveAsync(context.Slug, cancellationToken);

        return $"{context.Slug} removed";
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
