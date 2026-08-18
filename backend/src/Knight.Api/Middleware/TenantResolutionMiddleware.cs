using Microsoft.AspNetCore.Mvc;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Common;

namespace Knight.Api.Middleware;

/// <summary>
/// Extracts framework-specific request signals (host, authenticated principal) and
/// either elevates the request to the Platform context (only for an authenticated
/// platform-admin principal — never inferred from an absent tenant) or hands the
/// signals to <see cref="ITenantResolver"/> to populate the request-scoped
/// <see cref="ITenantContextAccessor"/>. This is the only place in the request
/// pipeline allowed to touch <see cref="HttpContext"/> for tenant resolution —
/// downstream code must consume <see cref="ITenantContext"/> instead.
///
/// Runs after authentication (see Program.cs) so <c>context.User</c> already
/// carries validated claims when this executes.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private const string PlatformAdminPrincipalType = "platform_admin";

    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITenantResolver tenantResolver, ITenantContextAccessor tenantContextAccessor)
    {
        // Infrastructure endpoints (health probes, etc.) must stay reachable even
        // when tenant resolution would otherwise depend on downstream infrastructure.
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var principalType = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst("principal_type")?.Value
            : null;

        if (principalType == PlatformAdminPrincipalType)
        {
            // Explicit, authenticated elevation — the only sanctioned way to enter
            // Platform context. Never inferred from a missing tenant elsewhere.
            tenantContextAccessor.SetPlatformContext();
            await _next(context);
            return;
        }

        var request = new TenantResolutionRequest(context.Request.Host.Host, context.User);
        var result = await tenantResolver.ResolveAsync(request, context.RequestAborted);

        switch (result.Outcome)
        {
            case TenantResolutionOutcome.Resolved:
                tenantContextAccessor.SetTenant(result.TenantId!.Value);
                await _next(context);
                return;

            case TenantResolutionOutcome.Conflict:
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "Tenant conflict.", ApiErrorCodes.Forbidden, result.FailureReason);
                return;

            case TenantResolutionOutcome.Blocked:
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "Tenant unavailable.", ApiErrorCodes.Forbidden, result.FailureReason);
                return;

            case TenantResolutionOutcome.NotResolved:
            default:
                // No tenant signal present — proceed with an empty tenant context.
                // Endpoints that require a tenant are responsible for rejecting
                // that (fail closed), never for defaulting to one.
                await _next(context);
                return;
        }
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string errorCode, string? detail)
    {
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.io/{statusCode}",
            Instance = context.Request.Path,
            Extensions = { ["errorCode"] = errorCode }
        };

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;

        return context.Response.WriteAsJsonAsync(problemDetails);
    }
}
