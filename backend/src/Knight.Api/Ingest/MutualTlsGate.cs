using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Stores;

namespace Knight.Api.Ingest;

/// <summary>
/// Enforces a store's client-certificate binding on every authenticated ingest
/// call.
///
/// The handshake checks the binding too, but a token minted then lives for half
/// an hour, and mutual TLS is worth having precisely because it is a *transport*
/// property that a stolen token does not carry with it. So the check runs per
/// request, not once per session.
///
/// The thumbprint comes either from a certificate this process terminated
/// itself, or from the header the terminating proxy sets. The header is only
/// trustworthy because the proxy is inside the trust boundary and strips it from
/// anything it did not verify — a deployment where that is not true must leave
/// mutual TLS off, and the option's documentation says so.
/// </summary>
internal sealed class MutualTlsGate : IEndpointFilter
{
    private readonly IStorePrincipal _principal;
    private readonly IStoreIntegrationService _stores;
    private readonly ILogger<MutualTlsGate> _logger;
    private readonly StoreOptions _options;

    public MutualTlsGate(
        IStorePrincipal principal,
        IStoreIntegrationService stores,
        ILogger<MutualTlsGate> logger,
        IOptions<StoreOptions> options)
    {
        _principal = principal;
        _stores = stores;
        _logger = logger;
        _options = options.Value;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (_principal.StoreId is not { } storeId)
        {
            return await next(context);
        }

        var binding = await _stores.GetMutualTlsBindingAsync(storeId, context.HttpContext.RequestAborted);

        if (binding is null || !binding.IsRequired)
        {
            return await next(context);
        }

        if (binding.IsSatisfiedBy(ReadThumbprint(context.HttpContext)))
        {
            return await next(context);
        }

        // Logged as a warning rather than returned in detail: the caller holds a
        // valid token and is failing the second factor, which is either a
        // misconfigured store or the exact case this exists to stop.
        _logger.LogWarning(
            "Store {StoreId} presented a token without the client certificate it is bound to.",
            storeId);

        return Results.Problem(
            title: "This store must connect with its client certificate.",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    private string? ReadThumbprint(HttpContext context)
    {
        if (context.Connection.ClientCertificate is { } certificate)
        {
            return Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }

        return context.Request.Headers.TryGetValue(_options.ClientCertificateHeader, out var header)
            ? header.ToString()
            : null;
    }
}
