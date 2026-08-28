using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent.Tests;

/// <summary>
/// What the store will and will not forward to a Feature's service.
///
/// The proxy is the one place in this library where a shopper's request meets
/// somebody else's server, so what is asserted here is mostly refusal: a method
/// the manifest did not declare, a caller the route does not allow, a Feature
/// whose shared secret has not arrived. The one positive test is that the
/// request which does go carries a signature and none of the caller's own
/// credentials.
/// </summary>
public sealed class ServiceProxyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "knight-proxy-" + Guid.NewGuid().ToString("n")[..8]);

    public ServiceProxyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Captures what the store sent, and answers instead of a service.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"service":"subscriptions"}""", Encoding.UTF8, "application/json"),
                Headers = { { "Set-Cookie", "sneaky=1" } },
            };
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Identity(string identity, string subject = "") : IKnightProxyIdentity
    {
        public (string Identity, string Subject) Describe(HttpContext context) => (identity, subject);
    }

    private async Task InstallAsync(string identityRequired = "anonymous", string[]? methods = null, bool withSecret = true)
    {
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
                Service = new ServiceEndpoint
                {
                    BaseUrl = "http://service.internal",
                    SecretName = "SUBSCRIPTIONS_SERVICE_SECRET",
                },
                ApiProxies =
                [
                    new ApiProxyRoute
                    {
                        Prefix = "subscribe/",
                        Upstream = "/api/v1/public/",
                        Methods = methods ?? ["GET"],
                        Identity = identityRequired,
                    },
                ],
            },
        });

        if (withSecret)
        {
            await FeatureConfigurationFile.WriteAsync(
                _root,
                "subscriptions",
                1,
                "{}",
                new Dictionary<string, string> { ["SUBSCRIPTIONS_SERVICE_SECRET"] = "issued-by-knight" });
        }
    }

    private async Task<(HttpContext Context, Recorder Recorder, bool ReachedTheStore)> SendAsync(
        string path,
        string method = "GET",
        IKnightProxyIdentity? identity = null,
        bool handshaken = true)
    {
        var recorder = new Recorder();
        var settings = Options.Create(new KnightOptions { FeatureRoot = _root });
        var reachedTheStore = false;
        var status = new KnightAgentStatus();

        if (handshaken)
        {
            // A store learns its own id from KNIGHT, and the proxy has to send
            // it: a service serves many shops and looks the caller up by it
            // before any cryptography happens.
            status.RecordHandshake(new StoreIdentity { StoreId = Guid.NewGuid(), StoreName = "A shop", Slug = "a-shop" });
        }

        var middleware = new KnightServiceProxyMiddleware(
            _ =>
            {
                reachedTheStore = true;
                return Task.CompletedTask;
            },
            new FeatureRegistryAccessor(settings),
            new Clients(recorder),
            identity ?? new Identity("anonymous"),
            status,
            settings,
            NullLogger<KnightServiceProxyMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.Headers["Cookie"] = "session=secret";
        context.Request.Headers["Authorization"] = "Bearer shopper-token";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        return (context, recorder, reachedTheStore);
    }

    [Fact]
    public async Task A_path_no_feature_claims_is_the_stores_own()
    {
        await InstallAsync();

        var (_, recorder, reachedTheStore) = await SendAsync("/products");

        Assert.True(reachedTheStore);
        Assert.Null(recorder.Request);
    }

    [Fact]
    public async Task A_declared_prefix_is_forwarded_and_the_answer_comes_back()
    {
        await InstallAsync();

        var (context, recorder, reachedTheStore) = await SendAsync("/subscribe/");

        Assert.False(reachedTheStore);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("http://service.internal/api/v1/public/", recorder.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_store_that_has_never_shaken_hands_cannot_name_itself()
    {
        await InstallAsync();

        var (context, recorder, _) = await SendAsync("/subscribe/", handshaken: false);

        // The service would refuse it as unsigned, which reads as a broken
        // signature rather than as a store that has not finished connecting.
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Null(recorder.Request);
    }

    [Fact]
    public async Task The_shoppers_credentials_never_leave_the_store()
    {
        await InstallAsync();

        var (_, recorder, _) = await SendAsync("/subscribe/");

        // The most important assertion in this file. A Feature's service holding
        // a credential it could replay against the store is exactly what
        // forwarding instead of loading is supposed to prevent.
        Assert.False(recorder.Request!.Headers.Contains("Cookie"));
        Assert.False(recorder.Request.Headers.Contains("Authorization"));
        Assert.True(recorder.Request.Headers.Contains("X-Knight-Signature"));
        Assert.True(recorder.Request.Headers.Contains("X-Knight-Store"));
        Assert.Equal("anonymous", recorder.Request.Headers.GetValues("X-Knight-Identity").Single());
    }

    [Fact]
    public async Task A_service_may_not_set_a_cookie_on_the_stores_domain()
    {
        await InstallAsync();

        var (context, _, _) = await SendAsync("/subscribe/");

        // That would be a session the store did not issue, on an origin the
        // service does not own.
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task A_method_the_manifest_did_not_declare_never_reaches_the_service()
    {
        await InstallAsync(methods: ["GET"]);

        var (context, recorder, _) = await SendAsync("/subscribe/", "DELETE");

        // A route that acquired a DELETE because nobody wrote a method list is a
        // read-only Feature that can now delete things.
        Assert.Equal(405, context.Response.StatusCode);
        Assert.Null(recorder.Request);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_on_a_customer_route()
    {
        await InstallAsync(identityRequired: "customer");

        var (context, recorder, _) = await SendAsync("/subscribe/", identity: new Identity("anonymous"));

        Assert.Equal(403, context.Response.StatusCode);
        Assert.Null(recorder.Request);
    }

    [Fact]
    public async Task A_customer_is_refused_on_a_staff_route()
    {
        await InstallAsync(identityRequired: "staff");

        var (context, _, _) = await SendAsync("/subscribe/", identity: new Identity("customer", "42"));

        // Enforced here as well as in the service. Two independent checks of one
        // rule is the arrangement worth having when one of the two is somebody
        // else's code.
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_feature_whose_secret_has_not_arrived_is_not_called_unsigned()
    {
        await InstallAsync(withSecret: false);

        var (context, recorder, _) = await SendAsync("/subscribe/");

        // An unsigned request is not a fallback. A service that accepted one
        // would accept anybody's.
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Null(recorder.Request);
    }

    [Fact]
    public async Task A_disabled_feature_stops_being_served_at_once()
    {
        await InstallAsync();
        await new FeatureRegistry(_root).SetEnabledAsync("subscriptions", false);

        var (_, recorder, reachedTheStore) = await SendAsync("/subscribe/");

        // An entitlement that lapsed is a commercial fact, and the registry is
        // read per request precisely so it takes effect now rather than at the
        // next restart.
        Assert.True(reachedTheStore);
        Assert.Null(recorder.Request);
    }
}
