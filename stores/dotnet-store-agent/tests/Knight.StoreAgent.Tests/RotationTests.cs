using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent.Tests;

/// <summary>
/// The store side of rotate-on-handshake (docs/hardening-backlog.md P2): when
/// KNIGHT hands back a replacement credential on a handshake, the agent adopts it
/// and authenticates with it from then on — the half without which a KNIGHT-side
/// rotation would simply lock the store out when the old secret's grace ended.
/// </summary>
public sealed class RotationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "knight-rotation-" + Guid.NewGuid().ToString("n")[..8]);

    public RotationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_handshake_that_returns_a_rotated_credential_is_adopted_for_next_time()
    {
        var options = new KnightOptions
        {
            FeatureRoot = _root,
            BaseUrl = "http://knight.internal",
            ClientId = "knight-shop-oldoldoldold",
            ClientSecret = "the-old-secret",
            Environment = "Production",
            Enabled = true,
            SigningKeys = { ["dev"] = "a-public-key" },
        };
        var settings = Options.Create(options);
        var connection = new KnightConnection(settings, new FileKnightCredentialStore(settings));

        var handler = new StubHandler(HandshakeResponse(
            rotatedClientId: "knight-shop-newnewnewnew",
            rotatedSecret: "the-new-secret"));

        var client = new KnightClient(
            new HttpClient(handler),
            settings,
            connection,
            new KnightAgentStatus(),
            NullLogger<KnightClient>.Instance);

        var identity = await client.HandshakeAsync(CancellationToken.None);

        // This handshake authenticated as the old credential, so the request it
        // sent carried the old one.
        Assert.Contains("knight-shop-oldoldoldold", handler.LastRequestBody);

        // But the replacement is now what is in force: the next handshake uses it.
        var current = await connection.CurrentAsync();
        Assert.Equal("knight-shop-newnewnewnew", current.ClientId);
        Assert.Equal("the-new-secret", current.ClientSecret);
        Assert.True(current.Enabled);

        // The connection carries over untouched — only the secret changed.
        Assert.Equal("http://knight.internal", current.BaseUrl);
        Assert.Equal("Production", current.Environment);

        // The handshake still returned this session's identity as usual.
        Assert.Equal("shop", identity.Slug);
    }

    [Fact]
    public async Task A_handshake_without_a_rotation_leaves_the_credential_alone()
    {
        var options = new KnightOptions
        {
            FeatureRoot = _root,
            BaseUrl = "http://knight.internal",
            ClientId = "knight-shop-stable00000",
            ClientSecret = "the-stable-secret",
            Environment = "Production",
            Enabled = true,
            SigningKeys = { ["dev"] = "a-public-key" },
        };
        var settings = Options.Create(options);
        var connection = new KnightConnection(settings, new FileKnightCredentialStore(settings));

        var client = new KnightClient(
            new HttpClient(new StubHandler(HandshakeResponse(rotatedClientId: null, rotatedSecret: null))),
            settings,
            connection,
            new KnightAgentStatus(),
            NullLogger<KnightClient>.Instance);

        await client.HandshakeAsync(CancellationToken.None);

        // Nothing was rotated, so nothing was written: the configured credential
        // still stands and no stored file has overtaken it.
        var current = await connection.CurrentAsync();
        Assert.Equal("knight-shop-stable00000", current.ClientId);
        Assert.Equal("the-stable-secret", current.ClientSecret);
    }

    private static string HandshakeResponse(string? rotatedClientId, string? rotatedSecret)
    {
        object? rotated = rotatedClientId is null
            ? null
            : new { clientId = rotatedClientId, clientSecret = rotatedSecret, expiresAt = DateTimeOffset.UtcNow.AddHours(1) };

        return JsonSerializer.Serialize(new
        {
            storeId = Guid.NewGuid(),
            storeName = "Shop",
            slug = "shop",
            environment = "Production",
            integrationStatus = "Connected",
            accessToken = "a-token",
            tokenType = "Bearer",
            expiresIn = 1800,
            rotatedCredential = rotated,
        });
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
