using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.ControlPlane.Security;
using Serilog.Context;

namespace Knight.Api.Middleware;

/// <summary>
/// Puts the identity of a request onto every log line it produces
/// (docs/observability.md §2).
///
/// A correlation id alone answers "which lines belong together". What an
/// operator actually asks during an incident is "which customer, which store,
/// which trace" — and if those are not on the line, answering means joining
/// against a database that is probably the thing being investigated. So they are
/// attached here, once, from the authenticated principal.
///
/// Deliberately not attached: anything that could be a secret. The token itself,
/// the session's refresh cookie, request bodies. What goes on a log line is who
/// the caller is, never what would let somebody become them.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>
    /// A caller-supplied id is echoed rather than trusted blindly, but it is
    /// still attacker-controlled text that ends up in a log. Length-capping it
    /// stops a caller writing a megabyte into every line.
    /// </summary>
    private const int MaxCorrelationIdLength = 128;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context);

        context.Response.Headers[HeaderName] = correlationId;

        // The trace id ties a log line to the span it happened inside, which is
        // what makes "this request was slow" and "this line was logged" the same
        // investigation rather than two.
        var traceId = Activity.Current?.TraceId.ToString();

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("TraceId", traceId))
        using (LogContext.PushProperty("PrincipalType", PrincipalTypeOf(context.User)))
        using (LogContext.PushProperty("UserId", ClaimOrNull(context.User, JwtRegisteredClaimNames.Sub)))
        using (LogContext.PushProperty("CustomerId", ClaimOrNull(context.User, ControlPlaneClaims.CustomerId)))
        using (LogContext.PushProperty("StoreId", ClaimOrNull(context.User, StoreClaims.StoreId)))
        {
            await _next(context);
        }
    }

    private static string Resolve(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var supplied))
        {
            return Guid.NewGuid().ToString("n");
        }

        var value = supplied.ToString();

        if (string.IsNullOrWhiteSpace(value))
        {
            return Guid.NewGuid().ToString("n");
        }

        // Newlines in a caller-supplied value would let it forge extra log
        // entries in a line-oriented sink.
        var sanitised = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return sanitised.Length <= MaxCorrelationIdLength
            ? sanitised
            : sanitised[..MaxCorrelationIdLength];
    }

    /// <summary>
    /// Which kind of caller this is. Present on every line so a store's traffic
    /// can be told from a dashboard user's without inspecting the route.
    /// </summary>
    private static string PrincipalTypeOf(ClaimsPrincipal? user) =>
        user?.FindFirstValue(PrincipalTypes.ClaimType) ?? "anonymous";

    private static string? ClaimOrNull(ClaimsPrincipal? user, string claimType) =>
        user?.FindFirstValue(claimType);
}
