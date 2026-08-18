using Identity;
using Identity.Authentication;
using Identity.Domain;
using Knight.Api.Composition;
using Knight.Application.Abstractions.Identity;
using Knight.Contracts.Auth;

namespace Knight.Api.Endpoints;

/// <summary>
/// Platform Super Admin authentication. Never authenticates a TenantUser
/// record — see docs/architecture/authorization.md.
/// </summary>
public static class PlatformAuthEndpoints
{
    public static void MapPlatformAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/auth").WithTags("Platform Auth");

        group.MapPost("/login", async (
                LoginRequest request,
                IPlatformAuthenticationService authService,
                IWebHostEnvironment environment,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var result = await authService.LoginAsync(request.Email, request.Password, cancellationToken);
                if (result.Outcome != LoginOutcome.Success)
                {
                    return AuthResponses.GenericUnauthorized();
                }

                httpContext.Response.AppendPlatformRefreshCookie(result.Session!.RawRefreshToken, result.Session.RefreshTokenExpiresAt, environment);
                return Results.Ok(AuthResponses.ToLoginResponse(result.Session));
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth-platform-login");

        group.MapPost("/refresh", async (
                HttpContext httpContext,
                IPlatformAuthenticationService authService,
                IWebHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                var rawToken = httpContext.Request.ReadPlatformRefreshCookie(environment);
                if (string.IsNullOrEmpty(rawToken))
                {
                    return AuthResponses.GenericUnauthorized();
                }

                var result = await authService.RefreshAsync(rawToken, cancellationToken);
                if (result.Outcome != RefreshOutcome.Success)
                {
                    httpContext.Response.DeletePlatformRefreshCookie(environment);
                    return AuthResponses.GenericUnauthorized();
                }

                httpContext.Response.AppendPlatformRefreshCookie(result.Session!.RawRefreshToken, result.Session.RefreshTokenExpiresAt, environment);
                return Results.Ok(AuthResponses.ToLoginResponse(result.Session));
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth-refresh");

        group.MapPost("/logout", async (
                HttpContext httpContext,
                IPlatformAuthenticationService authService,
                IWebHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                var rawToken = httpContext.Request.ReadPlatformRefreshCookie(environment);
                if (!string.IsNullOrEmpty(rawToken))
                {
                    await authService.LogoutAsync(rawToken, cancellationToken);
                }

                httpContext.Response.DeletePlatformRefreshCookie(environment);
                return Results.NoContent();
            })
            .AllowAnonymous();

        group.MapPost("/logout-all", async (
                HttpContext httpContext,
                ICurrentUser currentUser,
                IPlatformAuthenticationService authService,
                IWebHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                await authService.LogoutAllAsync(currentUser.UserId!.Value, cancellationToken);
                httpContext.Response.DeletePlatformRefreshCookie(environment);
                return Results.NoContent();
            })
            .RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/me", async (ICurrentUser currentUser, IPlatformAdminRepository repository, CancellationToken cancellationToken) =>
            {
                var admin = await repository.GetByIdAsync(currentUser.UserId!.Value, cancellationToken);
                if (admin is null)
                {
                    return Results.NotFound();
                }

                return Results.Ok(new CurrentPlatformAdminResponse
                {
                    Id = admin.Id,
                    Email = admin.Email,
                    DisplayName = admin.DisplayName,
                    Status = admin.Status.ToString()
                });
            })
            .RequireAuthorization("PlatformAdminOnly");

        group.MapPost("/change-password", async (
                ChangePasswordRequest request,
                ICurrentUser currentUser,
                IPlatformAuthenticationService authService,
                HttpContext httpContext,
                IWebHostEnvironment environment,
                CancellationToken cancellationToken) =>
            {
                var outcome = await authService.ChangePasswordAsync(currentUser.UserId!.Value, request.CurrentPassword, request.NewPassword, cancellationToken);

                if (outcome != ChangePasswordOutcome.Success)
                {
                    return AuthResponses.ToChangePasswordFailure(outcome);
                }

                httpContext.Response.DeletePlatformRefreshCookie(environment);
                return Results.NoContent();
            })
            .RequireAuthorization("PlatformAdminOnly");
    }
}
