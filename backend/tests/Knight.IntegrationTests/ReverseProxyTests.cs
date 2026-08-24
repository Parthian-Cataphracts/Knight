using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Knight.IntegrationTests;

/// <summary>
/// The host behind a TLS-terminating reverse proxy, which is how every
/// environment except a laptop runs it (docs/deployment.md section 4).
///
/// Two things about a request are not what they appear to be once a proxy is in
/// front: it arrives over plain HTTP however the client sent it, and it arrives
/// from the proxy's address rather than the caller's. Both matter here. The
/// second matters most — the sign-in and ingestion rate limiters partition on
/// the remote address, so a deployment that believes every request comes from
/// 127.0.0.1 gives the entire internet one shared bucket, and one caller
/// exhausting it locks out everybody.
///
/// A forwarded header is still only a claim, so the third test is the one that
/// keeps the other two safe: a claim from an address this deployment has not
/// named as a proxy is ignored.
/// </summary>
public sealed class ReverseProxyTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>An address that is emphatically not a proxy this deployment knows.</summary>
    private const string Stranger = "203.0.113.9";

    private readonly WebApplicationFactory<Program> _factory;

    public ReverseProxyTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            // Not Development: the forwarded-header handling is the same in every
            // environment, but the deployment being described is not a laptop.
            builder.UseEnvironment(Environments.Staging);

            builder.UseSetting("ConnectionStrings:ControlPlane", "Host=localhost;Database=knight_unused;Username=knight;Password=knight");

            // Both are refused at startup outside Development. Neither is reached
            // by the probe below, which is answered before any handler runs.
            builder.UseSetting("ConnectionStrings:Redis", "localhost:6379");
            builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-at-least-32-characters-long");
            builder.UseSetting("Stores:IntegrationSigningKey", "integration-test-store-signing-key-at-least-32-characters");

            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, ProxyProbe>());
        });
    }

    [Fact]
    public async Task TheSchemeTheProxyReportsIsTheSchemeTheRequestHas()
    {
        var probe = await ProbeAsync(from: "127.0.0.1", ("X-Forwarded-Proto", "https"));

        Assert.Equal("https", probe.Scheme);
    }

    [Fact]
    public async Task TheCallerBehindTheProxyIsTheRemoteAddress()
    {
        var probe = await ProbeAsync(from: "127.0.0.1", ("X-Forwarded-For", "198.51.100.7"));

        Assert.Equal("198.51.100.7", probe.RemoteAddress);
    }

    [Fact]
    public async Task ForwardedHeadersFromAnAddressThatIsNotAKnownProxyAreIgnored()
    {
        var probe = await ProbeAsync(
            from: Stranger,
            ("X-Forwarded-Proto", "https"),
            ("X-Forwarded-For", "198.51.100.7"));

        Assert.Equal("http", probe.Scheme);
        Assert.Equal(Stranger, probe.RemoteAddress);
    }

    private async Task<(string Scheme, string RemoteAddress)> ProbeAsync(
        string from,
        params (string Name, string Value)[] headers)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(ProxyProbe.RemoteAddressHeader, from);

        foreach (var (name, value) in headers)
        {
            client.DefaultRequestHeaders.Add(name, value);
        }

        var response = await client.GetAsync(ProxyProbe.Path);
        response.EnsureSuccessStatusCode();

        var parts = (await response.Content.ReadAsStringAsync()).Split(' ');
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Stands in for the network TestServer does not have.
    ///
    /// The first middleware gives the connection a remote address, because
    /// TestServer leaves it null and the forwarded-header middleware refuses a
    /// proxy it cannot identify — without this every test here would pass or fail
    /// for that reason instead of the one it is about.
    ///
    /// The last one runs after the real pipeline and reports what it decided. It
    /// is only ever reached by <see cref="Path"/>, which matches no endpoint.
    /// </summary>
    private sealed class ProxyProbe : IStartupFilter
    {
        public const string Path = "/__reverse-proxy-probe";

        public const string RemoteAddressHeader = "X-Test-Remote-Address";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteAddressHeader, out var address) &&
                    IPAddress.TryParse(address.ToString(), out var parsed))
                {
                    context.Connection.RemoteIpAddress = parsed;
                }

                await proceed();
            });

            next(app);

            app.Run(context => context.Response.WriteAsync(
                $"{context.Request.Scheme} {context.Connection.RemoteIpAddress}"));
        };
    }
}
