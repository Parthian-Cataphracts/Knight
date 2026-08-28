using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knight.StoreAgent;

/// <summary>
/// Who the store says is asking, for a request it forwards to a Feature's
/// service.
///
/// The store decides this and nothing else does. A Feature's service deciding
/// for itself whether a caller is staff would be the store trusting a third
/// party about its own users, and the assertion is the only identity the
/// service ever sees — no session cookie, no token, nothing the service could
/// replay against the store.
/// </summary>
public interface IKnightProxyIdentity
{
    /// <summary>
    /// <c>anonymous</c>, <c>customer</c> or <c>staff</c>, and the subject the
    /// store is prepared to assert.
    /// </summary>
    (string Identity, string Subject) Describe(HttpContext context);
}

/// <summary>
/// The default: whatever ASP.NET Core's authentication already decided.
///
/// A signed-in principal is a customer; one in an administrative role is staff.
/// A store whose roles are named differently supplies its own implementation —
/// which is the point of the interface, because getting this wrong is a shopper
/// reaching a merchant's screens.
/// </summary>
public sealed class ClaimsProxyIdentity : IKnightProxyIdentity
{
    /// <summary>Roles this store considers staff. Replace in a store that names them differently.</summary>
    public static IReadOnlySet<string> StaffRoles { get; set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Admin", "Owner", "Staff" };

    public (string Identity, string Subject) Describe(HttpContext context)
    {
        var user = context.User;

        if (user?.Identity is not { IsAuthenticated: true })
        {
            return ("anonymous", string.Empty);
        }

        var subject =
            user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? string.Empty;

        return StaffRoles.Any(user.IsInRole) ? ("staff", subject) : ("customer", subject);
    }
}

/// <summary>
/// Forwarding part of this store's URL space to a Feature's service.
///
/// The replacement for loading somebody's code into this process, and the
/// difference that matters is who runs it: a mounted assembly ran a Feature's
/// code with this application's database handle, and this makes an HTTP request
/// and returns what comes back.
///
/// Middleware rather than routes registered at start-up, deliberately. A Feature
/// is delivered while the shop is running, and a route table built once would
/// mean every install needing a restart before anything answered — which on a
/// store whose deploys are somebody else's business is the difference between
/// "installed" and "installed and useless".
/// </summary>
public sealed class KnightServiceProxyMiddleware(
    RequestDelegate next,
    FeatureRegistryAccessor registry,
    IHttpClientFactory clients,
    IKnightProxyIdentity identities,
    KnightAgentStatus status,
    IOptions<KnightOptions> options,
    ILogger<KnightServiceProxyMiddleware> logger)
{
    /// <summary>Named so a store can give it its own policy — timeouts, egress rules, tracing.</summary>
    public const string HttpClientName = "knight-feature-proxy";

    /// <summary>
    /// Request headers this store will never forward, whatever a Feature asks
    /// for.
    ///
    /// Everything carrying the shopper's identity or the store's. The Feature is
    /// told who is asking by <c>X-Knight-Identity</c>, which the store signs;
    /// anything it could replay is stripped. This is the most important list in
    /// the file.
    /// </summary>
    private static readonly HashSet<string> NeverForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "set-cookie", "x-csrftoken", "x-csrf-token",
        "proxy-authorization", "host", "content-length", "connection", "transfer-encoding",
    };

    /// <summary>
    /// Response headers this store will not pass back.
    ///
    /// A Feature's service must not be able to set a cookie on the store's
    /// domain: that is a session it did not issue, on an origin it does not own.
    /// </summary>
    private static readonly HashSet<string> NeverReturned = new(StringComparer.OrdinalIgnoreCase)
    {
        "set-cookie", "transfer-encoding", "connection", "content-encoding", "content-length",
    };

    private readonly KnightOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimStart('/') ?? string.Empty;

        // Read every request rather than cached: a Feature disabled a second ago
        // must stop being served now, not at the next restart. An entitlement
        // that lapsed is a commercial fact and the store enforces it.
        var features = await registry.AllAsync(context.RequestAborted);

        foreach (var feature in features.Values)
        {
            if (!feature.Enabled || feature.Contract?.Service is null)
            {
                continue;
            }

            foreach (var route in feature.Contract.ApiProxies)
            {
                var prefix = route.Prefix.Trim('/');

                if (prefix.Length == 0 || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await ForwardAsync(context, feature, route, path[prefix.Length..].TrimStart('/'));
                return;
            }
        }

        await next(context);
    }

    private async Task ForwardAsync(
        HttpContext context,
        InstalledFeature feature,
        ApiProxyRoute route,
        string remainder)
    {
        if (!route.Methods.Any(method => string.Equals(method, context.Request.Method, StringComparison.OrdinalIgnoreCase)))
        {
            // The store's own 405, which never reaches the service. A route that
            // acquired a DELETE because nobody wrote a method list is a
            // read-only Feature that can now delete things.
            await Refuse(context, StatusCodes.Status405MethodNotAllowed,
                $"{context.Request.Method} is not forwarded to {feature.Slug}.");
            return;
        }

        var (identity, subject) = identities.Describe(context);

        if (!Satisfies(route.Identity, identity))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "Not authorised.");
            return;
        }

        // Which store this is, by the id KNIGHT issued. A service serves many
        // shops and looks the caller up by it before any cryptography happens,
        // so a forwarded request without it is refused as unsigned — which is
        // what this store did until it started sending one.
        //
        // It cannot be configured: a store learns its own id at the handshake,
        // so a store that has never connected cannot name itself to anybody.
        var storeId = status.StoreId;

        if (string.IsNullOrEmpty(storeId))
        {
            logger.LogWarning(
                "A request for {Feature} arrived before this store had completed a handshake, "
                + "so it cannot identify itself to the service.",
                feature.Slug);

            await Refuse(context, StatusCodes.Status503ServiceUnavailable,
                "This store has not finished connecting to KNIGHT.");
            return;
        }

        var secretName = feature.Contract!.Service!.SecretName;
        var secret = FeatureConfigurationFile.SecretFor(_options.FeatureRoot, feature.Slug, secretName);

        if (string.IsNullOrEmpty(secret))
        {
            // An unsigned request is not a fallback. A service that accepted one
            // would accept anybody's, so a store that has not been given the
            // secret says so rather than trying.
            logger.LogError(
                "{Feature} has no {Secret} in its delivered configuration, so nothing can be forwarded to it.",
                feature.Slug,
                secretName);

            await Refuse(context, StatusCodes.Status503ServiceUnavailable,
                $"{feature.Slug} is not configured on this store.");
            return;
        }

        // The path **on the service**, not the path on the store. Both ends build
        // the canonical string independently and the service builds it from what
        // it received, so signing the store's own path would fail verification
        // with a signature that is perfectly correct about the wrong thing.
        var upstreamPath = "/" + $"{route.Upstream.Trim('/')}/{remainder}".Trim('/');

        if (context.Request.Path.Value?.EndsWith('/') == true && !upstreamPath.EndsWith('/'))
        {
            upstreamPath += "/";
        }

        var body = await ReadBodyAsync(context);

        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            new Uri(new Uri(feature.Contract.Service.BaseUrl.TrimEnd('/') + "/"), upstreamPath.TrimStart('/')
                + (context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty)));

        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);

            if (context.Request.ContentType is { Length: > 0 } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        foreach (var header in context.Request.Headers)
        {
            if (!NeverForwarded.Contains(header.Key))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        request.Headers.TryAddWithoutValidation("X-Knight-Store", storeId);
        request.Headers.TryAddWithoutValidation("X-Knight-Feature", feature.Slug);
        request.Headers.TryAddWithoutValidation("X-Knight-Identity", identity);
        request.Headers.TryAddWithoutValidation("X-Knight-Subject", subject);

        foreach (var (name, value) in Sign(secret, context.Request.Method, upstreamPath, body))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage answer;

        try
        {
            answer = await clients.CreateClient(HttpClientName).SendAsync(request, context.RequestAborted);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // 502, not 500. The store is fine and the thing it forwarded to is
            // not, and the two need different people looking at them.
            logger.LogWarning(exception, "Proxying to {Feature} failed.", feature.Slug);
            await Refuse(context, StatusCodes.Status502BadGateway, $"{feature.Slug} did not answer.");
            return;
        }

        using (answer)
        {
            context.Response.StatusCode = (int)answer.StatusCode;

            foreach (var header in answer.Headers.Concat(answer.Content.Headers))
            {
                if (!NeverReturned.Contains(header.Key))
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            await answer.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    /// <summary>
    /// Whether the caller is who the route requires.
    ///
    /// Enforced here, before anything is forwarded, and again by the service.
    /// Two independent checks of one rule is the arrangement worth having when
    /// one of the two is somebody else's code.
    /// </summary>
    private static bool Satisfies(string required, string actual) => required switch
    {
        "anonymous" => true,
        "staff" => actual == "staff",
        _ => actual is "customer" or "staff",
    };

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is 0 or null && !context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return [];
        }

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);

        return buffer.ToArray();
    }

    /// <summary>
    /// The headers that prove the request came from this store.
    ///
    /// HMAC-SHA256 over method, path, timestamp, nonce and a digest of the body.
    /// The body is covered so a proxy in the middle cannot change an order
    /// total; the timestamp is covered so a captured request stops working; the
    /// nonce is covered so it cannot be replayed inside the window the timestamp
    /// still allows.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> Sign(
        string secret,
        string method,
        string path,
        byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("n");
        var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var message = string.Join('\n', method.ToUpperInvariant(), path, timestamp, nonce, digest);

        var signature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message))).ToLowerInvariant();

        return
        [
            ("X-Knight-Timestamp", timestamp),
            ("X-Knight-Nonce", nonce),
            ("X-Knight-Signature", "sha256=" + signature),
            ("X-Knight-Skew-Seconds", "300"),
        ];
    }

    private static Task Refuse(HttpContext context, int status, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsJsonAsync(new { detail });
    }
}

public static class KnightProxyApplicationBuilderExtensions
{
    /// <summary>
    /// Serves every external Feature this store has been given.
    ///
    /// Place it after authentication — the store has to know who is asking
    /// before it can assert it — and before the store's own routing, so a
    /// Feature's prefix is reached rather than falling through to a 404.
    ///
    /// A prefix that collides with one of the store's own routes is served by
    /// the Feature, which is why the store's manifest review is where a
    /// collision is caught rather than here.
    /// </summary>
    public static IApplicationBuilder UseKnightFeatureProxy(this IApplicationBuilder app) =>
        app.UseMiddleware<KnightServiceProxyMiddleware>();
}
