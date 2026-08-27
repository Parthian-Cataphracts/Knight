using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent.Tests;

/// <summary>
/// A .NET store taking delivery of a Feature, through the whole step list
/// KNIGHT actually sends.
///
/// The step list here is KNIGHT's install pipeline verbatim, and that is the
/// point rather than a detail. The node reference store's fixture named the
/// eight verbs that store happened to implement, so the three it was missing
/// were invisible until a real job arrived naming eleven. A fixture that writes
/// its own vocabulary is a fixture that tests nothing.
/// </summary>
public sealed class DeliveryTests : IDisposable
{
    /// <summary>KNIGHT's install pipeline, in order. See <c>JobPipeline.InstallSteps</c>.</summary>
    private static readonly string[] InstallPipeline =
    [
        "preflight", "fetch", "verify", "backup", "install",
        "create-extensions", "migrate", "configure", "enable", "reload", "healthcheck",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "knight-dotnet-agent-" + Guid.NewGuid().ToString("n")[..8]);
    private readonly string _artifactPath;
    private readonly byte[] _artifact;
    private readonly string _digest;
    private readonly string _signature;
    private readonly string _publicKey;

    public DeliveryTests()
    {
        Directory.CreateDirectory(_root);

        // A real zip with a real assembly-shaped file in it. Built here rather
        // than checked in, because a fixture nobody can regenerate is one that
        // stops matching what the packaging tool produces.
        _artifactPath = Path.Combine(_root, "storefront-reports-1.0.0.zip");

        using (var stream = File.Create(_artifactPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(archive, "Knight.Feature.StorefrontReports.dll", "not a real assembly, but a real file");
            Write(archive, "knight_manifest.yaml", "slug: storefront-reports\nversion: 1.0.0\n");
        }

        _artifact = File.ReadAllBytes(_artifactPath);
        _digest = ArtifactVerifier.DigestOf(_artifact);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // ECDSA P-256 over the ASCII digest string, which is what
        // knight_package.py produces. The algorithm is the contract rather than
        // this store's choice.
        _signature = Convert.ToBase64String(key.SignData(
            Encoding.ASCII.GetBytes(_digest),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

        _publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private KnightOptions Options() => new()
    {
        FeatureRoot = Path.Combine(_root, "features"),
        Workspace = Path.Combine(_root, "workspace"),
        SigningKeys = new Dictionary<string, string> { ["dev"] = _publicKey },
        Enabled = false,
    };

    private JobRunner Runner(KnightOptions options)
    {
        var client = new KnightClient(new HttpClient(), Microsoft.Extensions.Options.Options.Create(options), NullLogger<KnightClient>.Instance);

        return new JobRunner(client, Microsoft.Extensions.Options.Options.Create(options), NullLogger<JobRunner>.Instance);
    }

    private AgentJob Job(
        string[]? steps = null,
        AgentArtifact? artifact = null,
        AgentRuntime? runtime = null,
        AgentMigrations? migrations = null)
        => new()
        {
            JobId = Guid.NewGuid(),
            Type = "Install",
            FeatureSlug = "storefront-reports",
            TargetVersion = "1.0.0",
            Steps = steps ?? InstallPipeline,
            Artifact = artifact ?? new AgentArtifact
            {
                PackageReference = "storefront-reports-1.0.0.zip",
                Digest = _digest,
                SizeBytes = _artifact.LongLength,
                Signature = _signature,
                SigningKeyId = "dev",
                DownloadUrl = new Uri(_artifactPath).AbsoluteUri,
            },
            Configuration = new AgentConfiguration { Version = 3, ValuesJson = """{"greeting":"delivered"}""" },
            Migrations = migrations ?? new AgentMigrations { Required = true, Reversible = true },
            Runtime = runtime ?? new AgentRuntime
            {
                Name = "dotnet",
                Namespace = "knight_storefront_reports",
                Module = "Knight.Feature.StorefrontReports",
                MountExport = "Knight.Feature.StorefrontReports.Endpoints",
                MountPrefix = "reports/",
            },
            HealthCheck = "Knight.Feature.StorefrontReports.Health#CheckAsync",
        };

    [Fact]
    public async Task ItInstallsAFeatureEndToEndAndReportsEveryStep()
    {
        var options = Options();
        var reported = new List<StepOutcome>();

        var outcome = await Runner(options).RunAsync(
            Job(),
            (step, _) => { reported.Add(step); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.True(outcome.Succeeded, $"{outcome.FailedStep}: {outcome.Code} — {outcome.Detail}");

        // One report per step, in KNIGHT's order. A job that reports only at the
        // end looks hung for the whole of a long migration and looks identical
        // to one that died.
        Assert.Equal(InstallPipeline, reported.Select(step => step.Step));
        Assert.All(reported, step => Assert.Equal("Succeeded", step.Status));

        var registry = new FeatureRegistry(options.FeatureRoot);
        var installed = await registry.FindAsync("storefront-reports");

        Assert.NotNull(installed);
        Assert.Equal("1.0.0", installed.Version);
        Assert.True(installed.Enabled);
        Assert.Equal(3, installed.ConfigVersion);
        Assert.Equal("1.0.0", await registry.MigrationStateAsync("knight_storefront_reports"));
    }

    [Fact]
    public async Task TheConfigurationIsWrittenBesideTheFeatureRatherThanInsideIt()
    {
        var options = Options();

        await Runner(options).RunAsync(Job(), null, CancellationToken.None);

        // Inside would be deleted by the next install, which is how a node
        // Feature came to read a config file its own upgrade had removed.
        Assert.True(File.Exists(Path.Combine(options.FeatureRoot, "storefront-reports.config.json")));
    }

    [Fact]
    public async Task ItRefusesAJobForAnotherRuntime()
    {
        var outcome = await Runner(Options()).RunAsync(
            Job(runtime: new AgentRuntime { Name = "django", Namespace = "x", Module = "x" }),
            null,
            CancellationToken.None);

        // Absent or different means this job was not meant for this store.
        // Installing it anyway would put a Django package on disk and report
        // success.
        Assert.Equal("preflight.wrong_runtime", outcome.Code);
    }

    [Fact]
    public async Task ItRefusesADownloadThatDoesNotHashToWhatTheJobSays()
    {
        var artifact = Job().Artifact! with { Digest = new string('0', 64) };

        var outcome = await Runner(Options()).RunAsync(Job(artifact: artifact), null, CancellationToken.None);

        Assert.Equal("verify", outcome.FailedStep);
        Assert.Equal("digest.mismatch", outcome.Code);
    }

    [Fact]
    public async Task ItRefusesAnArtifactSignedByAKeyItDoesNotTrust()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var forged = Convert.ToBase64String(stranger.SignData(
            Encoding.ASCII.GetBytes(_digest),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

        var outcome = await Runner(Options()).RunAsync(
            Job(artifact: Job().Artifact! with { Signature = forged }),
            null,
            CancellationToken.None);

        Assert.Equal("verify", outcome.FailedStep);
        Assert.Equal("signature.invalid", outcome.Code);
    }

    [Fact]
    public async Task ItRefusesAStepItHasNeverHeardOf()
    {
        var outcome = await Runner(Options()).RunAsync(
            Job(steps: ["preflight", "reticulate"]),
            null,
            CancellationToken.None);

        // Skipping would let a KNIGHT that had learnt a new verb believe this
        // store had performed it.
        Assert.Equal("step.unknown", outcome.Code);
    }

    [Fact]
    public async Task ItRefusesAFeatureNeedingADatabaseExtensionRatherThanPretending()
    {
        var outcome = await Runner(Options()).RunAsync(
            Job(migrations: new AgentMigrations { Required = true, Extensions = ["pg_trgm"] }),
            null,
            CancellationToken.None);

        // This library does not own the store's database and must not open one.
        // Succeeding here would tell KNIGHT the store is ready for a Feature
        // that fails the moment it runs.
        Assert.Equal("extensions.unsupported", outcome.Code);
    }

    [Fact]
    public async Task DisablingLeavesTheCodeAndTheDataAlone()
    {
        var options = Options();
        var runner = Runner(options);

        await runner.RunAsync(Job(), null, CancellationToken.None);
        await runner.RunAsync(Job(steps: ["disable"]), null, CancellationToken.None);

        var installed = await new FeatureRegistry(options.FeatureRoot).FindAsync("storefront-reports");

        // Disable is not uninstall. A customer who renews next week finds their
        // data where they left it.
        Assert.NotNull(installed);
        Assert.False(installed.Enabled);
        Assert.True(Directory.Exists(Path.Combine(options.FeatureRoot, "storefront-reports")));
    }

    [Fact]
    public async Task ARollbackRestoresWhatBackupKept()
    {
        var options = Options();
        var runner = Runner(options);

        await runner.RunAsync(Job(), null, CancellationToken.None);

        var target = Path.Combine(options.FeatureRoot, "storefront-reports");
        await File.WriteAllTextAsync(Path.Combine(target, "marker.txt"), "the version that was there");

        // A second install, which backs up the marked tree and replaces it.
        await runner.RunAsync(Job(), null, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(target, "marker.txt")));

        await runner.RunAsync(Job(steps: ["reverse-migrate", "restore-package"]), null, CancellationToken.None);

        // The backup is kept beside the Feature rather than in the job's
        // workspace, because a rollback is a different job and a workspace is
        // deleted when its job finishes.
        Assert.True(File.Exists(Path.Combine(target, "marker.txt")));
    }

    [Fact]
    public async Task AnArtifactThatReachesOutOfItsDirectoryIsRefused()
    {
        var escaping = Path.Combine(_root, "escaping.zip");

        using (var stream = File.Create(escaping))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(archive, "../../escaped.txt", "somewhere it was never given");
        }

        var bytes = File.ReadAllBytes(escaping);
        var digest = ArtifactVerifier.DigestOf(bytes);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var options = Options();
        options.SigningKeys["dev"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        var artifact = Job().Artifact! with
        {
            Digest = digest,
            SizeBytes = bytes.LongLength,
            DownloadUrl = new Uri(escaping).AbsoluteUri,
            Signature = Convert.ToBase64String(key.SignData(
                Encoding.ASCII.GetBytes(digest),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence)),
        };

        var outcome = await Runner(options).RunAsync(
            Job(steps: ["preflight", "fetch", "verify", "install"], artifact: artifact),
            null,
            CancellationToken.None);

        // A signed artifact is still not permission to write outside the
        // directory it was given. This is the oldest bug in unpacking and the
        // signature does not make it safe.
        Assert.Equal("install.path_traversal", outcome.Code);
    }
}
