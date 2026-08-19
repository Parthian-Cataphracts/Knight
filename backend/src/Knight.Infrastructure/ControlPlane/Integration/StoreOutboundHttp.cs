using System.Net;
using System.Net.Sockets;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// The one HTTP client KNIGHT uses to call stores.
///
/// It exists as a named client rather than a <c>new HttpClient()</c> anywhere
/// convenient so that the egress policy cannot be bypassed by adding a caller:
/// every outbound store request goes through a connect callback that resolves
/// the name itself, checks each address against
/// <see cref="IOutboundAddressPolicy"/>, and opens the socket to an address it
/// has just approved. Redirects are off — following one would hand the
/// destination back to the store — and the response size is capped, because a
/// store answering with an endless body is a denial of service against the
/// poller (docs/security-threat-model.md).
/// </summary>
public static class StoreOutboundHttp
{
    public const string ClientName = "store-integration";

    /// <summary>A health payload is a few hundred bytes; a megabyte is already an answer to a different question.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    public static IServiceCollection AddStoreOutboundHttp(this IServiceCollection services)
    {
        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var policy = provider.GetRequiredService<IOutboundAddressPolicy>();

                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    ConnectCallback = (context, cancellationToken) => ConnectAsync(policy, context, cancellationToken),
                };
            })
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<StoreProbeOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Knight-ControlPlane/1.0");
            });

        return services;
    }

    private static async ValueTask<Stream> ConnectAsync(
        IOutboundAddressPolicy policy,
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"'{host}' did not resolve to any address.");
        }

        var refusals = new List<string>();

        foreach (var address in addresses)
        {
            if (policy.Refuse(address) is { } reason)
            {
                refusals.Add($"{address}: {reason}");
                continue;
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        // Every address was refused. The message names them because an operator
        // debugging a store that will not verify needs to know that the domain
        // resolves somewhere KNIGHT will not go.
        throw new HttpRequestException(
            $"No permitted address for '{host}'. {string.Join("; ", refusals)}");
    }
}
