using Identity;
using Identity.Authentication;
using Identity.Domain;
using Knight.Api.Composition;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.Auth;

namespace Knight.Api.Endpoints;

/// <summary>
/// Tenant-staff authentication. Tenant identity always comes from host-based
/// tenant resolution (<see cref="ITenantContext"/>), never from the request
/// body — see docs/architecture/multi-tenancy.md. Never authenticates a
/// PlatformAdmin record.
/// </summary>
public static class TenantAuthEndpoints
{
    public static void MapTenantAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/auth").WithTags("Tenant Auth");

        group.MapPost("/login", async (
                LoginRequest request,
                ITenantContext tenantContext,
                ITenantAuthenticationService authService,
                IWebHostEnvironment environment,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                if (!tenantContext.HasTenant)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unable to resolve tenant from request.");
                }

                var result = await authService.LoginAsync(tenantContext.TenantId!.Value, request.Email, request.Password, cancellationToken);
                if (result.Outcome != LoginOutcome.Success)
                {
                    return AuthResponses.GenericUnauthorized();
                }

                httpContext.Response.AppendTenantRefreshCookie(result.Session!.RawRefreshToken, result.Session.RefreshTokenExpiresAt, environment);
                return Results.Ok(AuthResponses.ToLoginResponse(result.Session));
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth-tenant-login");

        group.MapPost("/refresh", async (
                HttpContext httpContext,
                ITenantContext tenantContext,
                ITenantAuthenticationService authService,
                IWebHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                if (!tenantContext.HasTenant)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unable to resolve tenant from request.");
                }

                var rawToken = httpContext.Request.ReadTenantRefreshCookie(environment);
                if (string.IsNullOrEmpty(rawToken))
                {
                    return AuthResponses.GenericUnauthorized();
                }

                var result = await authService.RefreshAsync(rawToken, tenantContext.TenantId!.Value, cancellationToken);
                if (result.Outcome != RefreshOutcome.Success)
                {
                    httpContext.Response.DeleteTenantRefreshCookie(environment);
                    return AuthResponses.GenericUnauthorized();
                }

                httpContext.Response.AppendTenantRefreshCookie(result.Session!.RawRefreshToken, result.Session.RefreshTokenExpiresAt, environment);
                return Results.Ok(AuthResponses.ToLoginResponse(result.Session));
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth-refresh");

        group.MapPost("/logout", async (
                HttpContext httpContext,
                ITenantAuthenticationService authService,
                IWebHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                var rawToken = httpContext.Request.ReadTenantRefreshCookie(environment);
                if (!string.IsNullOrEmpty(rawToken))
                {
                    await authService.LogoutAsync(rawToken, cancellationToken);
                }

                httpContext.Response.DeleteTenantRefreshCookie(environment);
                return Results.NoContent();
            })
            .AllowAnonymous();

        group.MapPost("/logout-all", async (
                HttpContext httpContext,
                ICurrentUser currentUser,
                ITenantAuthenticationService authService,
                IWebHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                await authService.LogoutAllAsync(currentUser.UserId!.Value, cancellationToken);
                httpContext.Response.DeleteTenantRefreshCookie(environment);
                return Results.NoContent();
            })
            .RequireAuthorization("TenantUserOnly");

        group.MapGet("/me", async (
                ICurrentUser currentUser,
                ITenantContext tenantContext,
                ITenantUserRepository repository,
                IEffectivePermissionService effectivePermissionService,
                CancellationToken cancellationToken) =>
            {
                if (!tenantContext.HasTenant)
                {
                    return Results.Forbid();
                }

                var user = await repository.GetByIdAsync(tenantContext.TenantId!.Value, currentUser.UserId!.Value, cancellationToken);
                if (user is null)
                {
                    return Results.NotFound();
                }

                var permissions = await effectivePermissionService.GetEffectivePermissionKeysAsync(tenantContext.TenantId!.Value, user.Id, cancellationToken);

                return Results.Ok(new CurrentTenantUserResponse
                {
                    Id = user.Id,
                    TenantId = user.TenantId,
                    Email = user.Email,
                    DisplayName = user.DisplayName,
                    Status = user.Status.ToString(),
                    EffectivePermissions = permissions
                });
            })
            .RequireAuthorization("TenantUserOnly");

        group.MapPost("/change-password", async (
                ChangePasswordRequest request,
                ICurrentUser currentUser,
                ITenantContext tenantContext,
                ITenantAuthenticationService authService,
                HttpContext httpContext,
                IWebHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                if (!tenantContext.HasTenant)
                {
                    return Results.Forbid();
                }

                var outcome = await authService.ChangePasswordAsync(tenantContext.TenantId!.Value, currentUser.UserId!.Value, request.CurrentPassword, request.NewPassword, cancellationToken);

                if (outcome != ChangePasswordOutcome.Success)
                {
                    return AuthResponses.ToChangePasswordFailure(outcome);
                }

                httpContext.Response.DeleteTenantRefreshCookie(environment);
                return Results.NoContent();
            })
            .RequireAuthorization("TenantUserOnly");
    }
}
