using Microsoft.Extensions.Options;

namespace Knight.StoreAgent.Tests;

/// <summary>
/// Connecting a store from its own panel rather than from a deploy.
///
/// Until this existed, a credential could only arrive in configuration, so
/// connecting a shop to KNIGHT meant an environment variable and a restart —
/// which a merchant cannot do, and which turns "connect us" into "send your
/// client secret to whoever can".
///
/// Two properties are worth pinning: what a stored credential does to the one in
/// configuration, and what disconnecting does **not** do.
/// </summary>
public sealed class ConnectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "knight-connection-" + Guid.NewGuid().ToString("n")[..8]);

    public ConnectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private KnightConnection Connection(KnightOptions? options = null)
    {
        var settings = Options.Create(options ?? new KnightOptions { FeatureRoot = _root });

        return new KnightConnection(settings, new FileKnightCredentialStore(settings));
    }

    [Fact]
    public async Task Configuration_alone_is_the_credential_when_nothing_has_been_entered()
    {
        var connection = Connection(new KnightOptions
        {
            FeatureRoot = _root,
            BaseUrl = "http://knight.internal",
            ClientId = "from-configuration",
            ClientSecret = "a-secret",
            Enabled = true,
        });

        var credential = await connection.CurrentAsync();

        // A store deployed with a credential must not need somebody to press a
        // button as well.
        Assert.Equal("from-configuration", credential.ClientId);
        Assert.True(credential.Enabled);
        Assert.True(credential.IsComplete);
    }

    [Fact]
    public async Task What_an_operator_entered_wins_over_configuration()
    {
        var connection = Connection(new KnightOptions
        {
            FeatureRoot = _root,
            ClientId = "from-configuration",
            ClientSecret = "an-old-secret",
        });

        await connection.ConnectAsync(new KnightCredential
        {
            BaseUrl = "http://knight.internal",
            ClientId = "entered-in-the-panel",
            ClientSecret = "the-new-secret",
        });

        var credential = await connection.CurrentAsync();

        // Somebody typing a credential into a panel has said something more
        // recent than whatever was in the environment when the container
        // started.
        Assert.Equal("entered-in-the-panel", credential.ClientId);
        Assert.True(credential.Enabled);
    }

    [Fact]
    public async Task Connecting_turns_the_agent_on_without_a_restart()
    {
        var connection = Connection();

        Assert.False((await connection.CurrentAsync()).Enabled);

        await connection.ConnectAsync(new KnightCredential
        {
            BaseUrl = "http://knight.internal",
            ClientId = "a-client",
            ClientSecret = "a-secret",
        });

        // The whole point. The background services read this each pass, so a
        // store connected at ten past the hour is polling for work at eleven
        // past without anybody redeploying it.
        Assert.True((await connection.CurrentAsync()).Enabled);
    }

    [Fact]
    public async Task An_incomplete_stored_credential_falls_back_rather_than_half_connecting()
    {
        var connection = Connection(new KnightOptions
        {
            FeatureRoot = _root,
            ClientId = "from-configuration",
            ClientSecret = "a-secret",
        });

        await connection.ConnectAsync(new KnightCredential { BaseUrl = "http://knight.internal" });

        // Half a credential is not a credential. Using it would mean a store
        // handshaking with an empty client id and reporting a refusal that
        // names the wrong problem.
        Assert.Equal("from-configuration", (await connection.CurrentAsync()).ClientId);
    }

    [Fact]
    public async Task Disconnecting_forgets_the_credential_and_leaves_the_features_alone()
    {
        var registry = new FeatureRegistry(_root);
        await registry.RecordAsync(new InstalledFeature { Slug = "subscriptions", Version = "2.1.0", Enabled = true });

        var connection = Connection();
        await connection.ConnectAsync(new KnightCredential
        {
            BaseUrl = "http://knight.internal",
            ClientId = "a-client",
            ClientSecret = "a-secret",
        });

        await connection.DisconnectAsync();

        Assert.False((await connection.CurrentAsync()).IsComplete);

        // Disconnecting is not uninstalling. What a merchant means by it is
        // "stop talking to them", and deleting a shop's Features because
        // somebody pressed the wrong button is unrecoverable from here.
        Assert.NotNull(await registry.FindAsync("subscriptions"));
    }

    [Fact]
    public async Task A_status_snapshot_carries_no_secret()
    {
        var settings = Options.Create(new KnightOptions { FeatureRoot = _root });
        var connection = new KnightConnection(settings, new FileKnightCredentialStore(settings));

        await connection.ConnectAsync(new KnightCredential
        {
            BaseUrl = "http://knight.internal",
            ClientId = "a-client",
            ClientSecret = "the-secret-nobody-may-read-back",
        });

        var reader = new KnightStatusReader(
            connection,
            new KnightAgentStatus(),
            new FeatureRegistryAccessor(settings),
            settings);

        var status = await reader.ReadAsync();
        var rendered = System.Text.Json.JsonSerializer.Serialize(status);

        // A connection screen answers "is this working and when did it last
        // work". Every one of those questions is answerable without a
        // credential, which is what makes the screen safe to look at.
        Assert.Equal("a-client", status.ClientId);
        Assert.DoesNotContain("the-secret-nobody-may-read-back", rendered);
    }

    [Fact]
    public async Task The_status_reports_what_has_been_delivered_and_whether_it_can_be_served()
    {
        var settings = Options.Create(new KnightOptions { FeatureRoot = _root });
        var registry = new FeatureRegistry(_root);

        await registry.RecordAsync(new InstalledFeature
        {
            Slug = "subscriptions",
            Version = "2.1.0",
            Enabled = true,
            Contract = new ExternalContract
            {
                Architecture = "external_service",
                Slug = "subscriptions",
                Service = new ServiceEndpoint { BaseUrl = "http://localhost:8100", SecretName = "SUBSCRIPTIONS_SERVICE_SECRET" },
                ApiProxies = [new ApiProxyRoute { Prefix = "subscriptions/", Upstream = "/api/v1/subscriptions/" }],
                UiMounts = [new UiMount { Slot = "admin.sidebar", Label = "Subscriptions", Path = "/admin/subscriptions" }],
            },
        });

        var connection = new KnightConnection(settings, new FileKnightCredentialStore(settings));
        var reader = new KnightStatusReader(connection, new KnightAgentStatus(), new FeatureRegistryAccessor(settings), settings);

        var before = (await reader.ReadAsync()).Features.Single();

        // No configuration has arrived, so nothing can be signed. A Feature in
        // that state looks identical to a working one from the outside, right
        // up until the first request it forwards is refused.
        Assert.False(before.HasServiceSecret);
        Assert.Equal(["subscriptions/"], before.ProxyPrefixes);
        Assert.Equal("admin.sidebar", before.UiMounts.Single().Slot);

        await FeatureConfigurationFile.WriteAsync(
            _root,
            "subscriptions",
            4,
            "{}",
            new Dictionary<string, string> { ["SUBSCRIPTIONS_SERVICE_SECRET"] = "issued-by-knight" });

        Assert.True((await reader.ReadAsync()).Features.Single().HasServiceSecret);
    }

    [Fact]
    public async Task A_delivered_configuration_keeps_its_secrets()
    {
        await FeatureConfigurationFile.WriteAsync(
            _root,
            "subscriptions",
            4,
            """{"retry_attempts":3}""",
            new Dictionary<string, string> { ["SUBSCRIPTIONS_SERVICE_SECRET"] = "issued-by-knight" });

        // The defect this file was written for: the .NET agent wrote the values
        // document alone, so the shared secret KNIGHT issues per store arrived
        // here and was thrown away.
        Assert.Equal(
            "issued-by-knight",
            FeatureConfigurationFile.SecretFor(_root, "subscriptions", "SUBSCRIPTIONS_SERVICE_SECRET"));

        var written = await File.ReadAllTextAsync(FeatureConfigurationFile.PathFor(_root, "subscriptions"));
        Assert.Contains("retry_attempts", written);
    }

    [Fact]
    public void A_secret_this_store_was_never_given_is_empty_rather_than_an_exception()
    {
        // On a shopper's request path. A missing configuration is a Feature that
        // cannot be forwarded to, which the caller already handles; a throw here
        // would turn it into a 500 on somebody's checkout.
        Assert.Equal(string.Empty, FeatureConfigurationFile.SecretFor(_root, "nothing", "ANY"));
    }
}
