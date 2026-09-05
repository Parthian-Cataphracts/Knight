using Microsoft.Extensions.Options;

namespace Knight.StoreAgent.Tests;

/// <summary>
/// Phase 32B — the store's menu shows a Feature's UI only while the store is
/// entitled to it. A lapsed entitlement disables the Feature, and everything that
/// renders — here, the visible-mounts list a shop draws its menu from — must drop
/// it, not merely refuse the API behind it.
/// </summary>
public sealed class EntitlementUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "knight-entui-" + Guid.NewGuid().ToString("n")[..8]);

    public EntitlementUiTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static InstalledFeature Subscriptions(bool enabled) => new()
    {
        Slug = "subscriptions",
        Version = "2.1.0",
        Enabled = enabled,
        Contract = new ExternalContract
        {
            Architecture = "external_service",
            Slug = "subscriptions",
            Service = new ServiceEndpoint { BaseUrl = "http://localhost:8100", SecretName = "SUBSCRIPTIONS_SERVICE_SECRET" },
            UiMounts = [new UiMount { Slot = "admin.sidebar", Label = "Subscriptions", Path = "/admin/subscriptions" }],
        },
    };

    private KnightStatusReader Reader(IOptions<KnightOptions> settings) =>
        new(new KnightConnection(settings, new FileKnightCredentialStore(settings)), new KnightAgentStatus(),
            new FeatureRegistryAccessor(settings), settings);

    [Fact]
    public async Task An_entitled_feature_contributes_its_mount_to_the_menu()
    {
        var settings = Options.Create(new KnightOptions { FeatureRoot = _root, SigningKeys = { ["dev"] = "k" } });
        await new FeatureRegistry(_root).RecordAsync(Subscriptions(enabled: true));

        var status = await Reader(settings).ReadAsync();

        var mount = Assert.Single(status.VisibleUiMounts);
        Assert.Equal("subscriptions", mount.Slug);
        Assert.Equal("admin.sidebar", mount.Slot);
        Assert.Equal("/admin/subscriptions", mount.Path);
    }

    [Fact]
    public async Task A_lapsed_entitlement_removes_the_mount_from_the_menu()
    {
        var settings = Options.Create(new KnightOptions { FeatureRoot = _root, SigningKeys = { ["dev"] = "k" } });
        var registry = new FeatureRegistry(_root);
        await registry.RecordAsync(Subscriptions(enabled: true));

        // Entitlement withdrawn → KNIGHT disables the Feature. Its code and data
        // stay; the menu must not.
        await registry.SetEnabledAsync("subscriptions", enabled: false);

        var status = await Reader(settings).ReadAsync();

        Assert.Empty(status.VisibleUiMounts);

        // The Feature is still reported as installed-but-disabled, so a management
        // screen can show why the shop's menu is shorter — the report keeps the
        // full picture even though the shop's menu does not.
        var feature = Assert.Single(status.Features);
        Assert.False(feature.Enabled);
        Assert.Single(feature.UiMounts);
    }
}
