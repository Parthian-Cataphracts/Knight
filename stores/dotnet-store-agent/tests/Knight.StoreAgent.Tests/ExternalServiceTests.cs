using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Knight.StoreAgent.Tests;

/// <summary>
/// A .NET store taking delivery of a Feature that is a service.
///
/// The store runs none of its code, so most of what is asserted here is
/// refusal: an event this store does not publish, a slot it does not offer, a
/// signed document that disagrees with the job it arrived on
/// (<c>adr/0033</c>).
///
/// The step list is KNIGHT's external install pipeline verbatim, and every verb
/// in it is one the in-process pipeline already had. That is the property that
/// makes the pivot safe for a store nobody has redeployed, and it is asserted
/// rather than assumed.
/// </summary>
public sealed class ExternalServiceTests : IDisposable
{
    /// <summary>KNIGHT's external install pipeline. See <c>JobPipeline.ExternalInstallSteps</c>.</summary>
    private static readonly string[] Pipeline =
        ["preflight", "fetch", "verify", "backup", "configure", "install", "enable", "healthcheck"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "knight-external-" + Guid.NewGuid().ToString("n")[..8]);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ExternalServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _key.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private KnightOptions Options() => new()
    {
        FeatureRoot = Path.Combine(_root, "features"),
        Workspace = Path.Combine(_root, "workspace"),
        SigningKeys = new Dictionary<string, string> { ["dev"] = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()) },
        Enabled = false,
    };

    private JobRunner Runner(KnightOptions options)
    {
        var settings = Microsoft.Extensions.Options.Options.Create(options);
        var client = new KnightClient(
            new HttpClient(),
            settings,
            new KnightConnection(settings, new FileKnightCredentialStore(settings)),
            new KnightAgentStatus(),
            NullLogger<KnightClient>.Instance);

        return new JobRunner(client, Microsoft.Extensions.Options.Options.Create(options), NullLogger<JobRunner>.Instance);
    }

    private static string Document(
        string version = "2.0.0",
        string architecture = "external_service",
        string webhookEvent = "order.placed",
        string slot = "admin.sidebar",
        string baseUrl = "https://subscriptions.knight.dev")
        => $$"""
            {
              "apiVersion": "knight.dev/v1",
              "architecture": "{{architecture}}",
              "slug": "subscriptions",
              "version": "{{version}}",
              "name": "Subscriptions",
              "service": {
                "base_url": "{{baseUrl}}",
                "auth": "hmac-sha256",
                "health": "/healthz",
                "secret": "SUBSCRIPTIONS_SERVICE_SECRET"
              },
              "webhooks": [ { "event": "{{webhookEvent}}", "path": "/hooks/in", "delivery": "at-least-once" } ],
              "api_proxies": [ { "prefix": "subscriptions/", "upstream": "/api/v1/", "methods": ["GET", "POST"], "identity": "customer" } ],
              "ui_mounts": [ { "slot": "{{slot}}", "label": "Subscriptions", "path": "/admin", "kind": "iframe" } ]
            }
            """;

    private AgentJob Job(string? document = null, string[]? steps = null, string type = "Install", string? digest = null)
    {
        var body = Encoding.UTF8.GetBytes(document ?? Document());
        var path = Path.Combine(_root, $"subscriptions-{Guid.NewGuid():n}.json");
        File.WriteAllBytes(path, body);

        var real = ArtifactVerifier.DigestOf(body);
        var signature = Convert.ToBase64String(_key.SignData(
            Encoding.ASCII.GetBytes(digest ?? real),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

        return new AgentJob
        {
            JobId = Guid.NewGuid(),
            Type = type,
            FeatureSlug = "subscriptions",
            TargetVersion = JsonDocument.Parse(body).RootElement.GetProperty("version").GetString(),
            // The field that tells the agent what the bytes it is about to fetch
            // *are*: an archive to unpack, or a document to read.
            Architecture = "external_service",
            Steps = steps ?? Pipeline,
            Artifact = new AgentArtifact
            {
                PackageReference = Path.GetFileName(path),
                Digest = digest ?? real,
                SizeBytes = body.LongLength,
                Signature = signature,
                SigningKeyId = "dev",
                DownloadUrl = new Uri(path).AbsoluteUri,
            },
            Configuration = new AgentConfiguration { Version = 3, ValuesJson = """{"plan":"monthly"}""" },
            Runtime = new AgentRuntime { Name = "external", Namespace = "knight_subscriptions", Module = "subscriptions" },
        };
    }

    [Fact]
    public async Task ItRegistersWebhooksRoutesAndScreensWithoutUnpackingAnything()
    {
        var options = Options();
        var reported = new List<StepOutcome>();

        var outcome = await Runner(options).RunAsync(
            Job(),
            (step, _) => { reported.Add(step); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.True(outcome.Succeeded, $"{outcome.FailedStep}: {outcome.Code} — {outcome.Detail}");

        // No migrate step was named and none ran. This Feature has no schema in
        // this store, so there is nothing to migrate and nothing to reverse.
        Assert.DoesNotContain("migrate", reported.Select(step => step.Step));

        var installed = await new FeatureRegistry(options.FeatureRoot).FindAsync("subscriptions");

        Assert.NotNull(installed);
        Assert.True(installed.IsExternalService);
        Assert.True(installed.Enabled);
        Assert.Single(installed.Contract!.Webhooks);
        Assert.Single(installed.Contract.ApiProxies);
        Assert.Single(installed.Contract.UiMounts);
    }

    [Fact]
    public async Task NoPackageDirectoryIsCreatedBecauseThereIsNoPackage()
    {
        var options = Options();

        await Runner(options).RunAsync(Job(), null, CancellationToken.None);

        // A directory here would be somewhere made for code that does not
        // exist, which the next person to look would read as a half-finished
        // install.
        Assert.False(Directory.Exists(Path.Combine(options.FeatureRoot, "subscriptions")));
        Assert.True(File.Exists(Path.Combine(options.FeatureRoot, "subscriptions.config.json")));
    }

    [Fact]
    public async Task TheRegistryEntryNamesNoAssemblyToLoad()
    {
        var options = Options();

        await Runner(options).RunAsync(Job(), null, CancellationToken.None);

        var installed = await new FeatureRegistry(options.FeatureRoot).FindAsync("subscriptions");

        // A plausible-looking value here would have the store try to load an
        // assembly that was never delivered.
        Assert.Equal(string.Empty, installed!.Module);
        Assert.Null(installed.MountType);
    }

    [Fact]
    public async Task ItRefusesAnEventThisStoreDoesNotPublish()
    {
        var outcome = await Runner(Options()).RunAsync(
            Job(Document(webhookEvent: "order.plaecd")),
            null,
            CancellationToken.None);

        // Without this the Feature installs cleanly, passes its health check
        // and never hears anything. KNIGHT cannot make this check: it does not
        // know what any particular store publishes.
        Assert.Equal("install.unknown_event", outcome.Code);
    }

    [Fact]
    public async Task ItRefusesASlotThisStoreDoesNotOffer()
    {
        var outcome = await Runner(Options()).RunAsync(
            Job(Document(slot: "admin.nowhere")),
            null,
            CancellationToken.None);

        Assert.Equal("install.unknown_slot", outcome.Code);
    }

    [Fact]
    public async Task ItRefusesASignedDocumentThatDisagreesWithTheJob()
    {
        var outcome = await Runner(Options()).RunAsync(
            Job(Document(architecture: "in_process")),
            null,
            CancellationToken.None);

        // Acting on either would be choosing which of two disagreeing sources
        // to trust, and the honest answer is neither.
        Assert.Equal("install.wrong_architecture", outcome.Code);
    }

    [Fact]
    public async Task ATamperedConfigurationNeverReachesTheRegistry()
    {
        var options = Options();

        var outcome = await Runner(options).RunAsync(
            Job(digest: new string('0', 64)),
            null,
            CancellationToken.None);

        // The reason the configuration is signed at all: without this the store
        // would wire a proxy route, carrying its customers' requests, to
        // whatever host answered the download URL.
        Assert.Equal("verify", outcome.FailedStep);
        Assert.Equal("digest.mismatch", outcome.Code);
        Assert.Null(await new FeatureRegistry(options.FeatureRoot).FindAsync("subscriptions"));
    }

    [Fact]
    public async Task DisableStopsItServingAndUninstallUnregistersIt()
    {
        var options = Options();
        var runner = Runner(options);
        var registry = new FeatureRegistry(options.FeatureRoot);

        await runner.RunAsync(Job(), null, CancellationToken.None);
        await runner.RunAsync(Job(steps: ["disable"], type: "Disable"), null, CancellationToken.None);

        var installed = await registry.FindAsync("subscriptions");

        // Disable is not uninstall. The registration stays so that re-entitling
        // the customer next week does not need the whole delivery again.
        Assert.NotNull(installed);
        Assert.False(installed.Enabled);
        Assert.Empty(await registry.ExternalFeaturesAsync());

        await runner.RunAsync(
            Job(steps: ["disable", "backup", "remove-package"], type: "Uninstall"),
            null,
            CancellationToken.None);

        Assert.Null(await registry.FindAsync("subscriptions"));
    }

    [Fact]
    public async Task ARollbackRestoresTheRegistrationTheBackupKept()
    {
        var options = Options();
        var runner = Runner(options);
        var registry = new FeatureRegistry(options.FeatureRoot);

        await runner.RunAsync(Job(), null, CancellationToken.None);
        await runner.RunAsync(Job(Document(version: "2.1.0", webhookEvent: "order.paid")), null, CancellationToken.None);

        Assert.Equal("2.1.0", (await registry.FindAsync("subscriptions"))!.Version);

        var outcome = await runner.RunAsync(
            Job(steps: ["restore-package", "configure", "enable", "healthcheck"], type: "Rollback"),
            null,
            CancellationToken.None);

        Assert.True(outcome.Succeeded, $"{outcome.FailedStep}: {outcome.Code} — {outcome.Detail}");

        var restored = await registry.FindAsync("subscriptions");

        // Restored from the local copy `backup` kept, not fetched: a rollback
        // job names the version it is rolling *to* and carries the artifact of
        // the one it is rolling *from*.
        Assert.Equal("2.0.0", restored!.Version);
        Assert.True(restored.Enabled);
        Assert.Equal("order.placed", restored.Contract!.Webhooks[0].Event);
    }

    [Fact]
    public async Task ARollbackWithNothingKeptFailsRatherThanReportingSuccess()
    {
        var outcome = await Runner(Options()).RunAsync(
            Job(steps: ["restore-package"], type: "Rollback"),
            null,
            CancellationToken.None);

        // An operator told the store is back on the old version stops looking.
        Assert.Equal("rollback.no_backup", outcome.Code);
    }

    [Fact]
    public async Task OnlyTheFeaturesThatSubscribedAreTold()
    {
        var options = Options();
        var runner = Runner(options);
        var registry = new FeatureRegistry(options.FeatureRoot);

        await runner.RunAsync(Job(), null, CancellationToken.None);

        Assert.Single(await registry.SubscribersForAsync("order.placed"));
        Assert.Empty(await registry.SubscribersForAsync("order.refunded"));

        await runner.RunAsync(Job(steps: ["disable"], type: "Disable"), null, CancellationToken.None);

        // An entitlement that lapsed is a commercial fact and the store
        // enforces it now, not at the next restart.
        Assert.Empty(await registry.SubscribersForAsync("order.placed"));
    }

    [Fact]
    public void EveryVerbTheExternalPipelineNamesIsOneThisStoreAlreadyImplements()
    {
        var options = Options();
        var runner = Runner(options);

        // The property that makes this pivot safe for a store nobody has
        // redeployed. Asserted by running a job that names the whole pipeline
        // and checking no step came back unknown — a store that meets an
        // unknown verb refuses the entire job.
        var outcome = runner.RunAsync(Job(), null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.NotEqual("step.unknown", outcome.Code);
    }
}
