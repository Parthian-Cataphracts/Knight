using AccessControl;
using Knight.Contracts.ControlPlane;
using Onboarding;
using LoginCommand = AccessControl.LoginRequest;
using LoginContract = Knight.Contracts.ControlPlane.LoginRequest;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Dashboard authentication (docs/api-contracts.md section 2).
///
/// The refresh token is returned in an HttpOnly, Secure, SameSite=Strict cookie
/// as well as in the body: browsers use the cookie and never touch the value,
/// while service clients and tests read the body
/// (docs/authentication.md section 1).
/// </summary>
public static class ControlPlaneAuthEndpoints
{
    private const string RefreshCookieName = "knight_refresh";

    public static void MapControlPlaneAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth").WithTags("Control Plane Auth");

        group.MapPost("/login", async (
            LoginContract request,
            HttpContext http,
            IControlPlaneAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.LoginAsync(
                new LoginCommand(
                    request.Email,
                    request.Password,
                    request.MfaCode,
                    http.Connection.RemoteIpAddress?.ToString(),
                    http.Request.Headers.UserAgent.ToString()),
                cancellationToken);

            return result.Outcome switch
            {
                AuthenticationOutcome.InvalidCredentials => Results.Problem(
                    title: "Invalid credentials.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "unauthorized" }),

                // No tokens yet: the caller re-posts the same credentials with a code.
                AuthenticationOutcome.MfaRequired => Results.Ok(new LoginResponse { Status = "mfa_required" }),

                _ => WriteSession(http, result),
            };
        }).AllowAnonymous().RequireRateLimiting("auth-control-plane");

        group.MapPost("/refresh", async (
            RefreshRequest? request,
            HttpContext http,
            IControlPlaneAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            var token = request?.RefreshToken ?? http.Request.Cookies[RefreshCookieName];

            var result = await service.RefreshAsync(
                token ?? string.Empty,
                http.Connection.RemoteIpAddress?.ToString(),
                http.Request.Headers.UserAgent.ToString(),
                cancellationToken);

            return result.Outcome is AuthenticationOutcome.Succeeded
                ? WriteSession(http, result)
                : Results.Problem(
                    title: "The refresh token is not valid.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "unauthorized" });
        }).AllowAnonymous().RequireRateLimiting("auth-control-plane");

        group.MapPost("/logout", async (
            RefreshRequest? request,
            HttpContext http,
            IControlPlaneAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            var token = request?.RefreshToken ?? http.Request.Cookies[RefreshCookieName];
            await service.LogoutAsync(token ?? string.Empty, cancellationToken);

            http.Response.Cookies.Delete(RefreshCookieName);
            return Results.NoContent();
        }).AllowAnonymous();

        // Anonymous by nature: whoever follows an invitation link is not signed
        // in yet. The token is the whole of the proof, and it is rate-limited on
        // the same policy as login because it is a credential-guessing surface
        // in exactly the same way.
        group.MapPost("/activate", async (
            ActivateAccountRequest request,
            IAccountAdministration administration,
            CancellationToken cancellationToken) =>
        {
            await administration.CompleteActivationAsync(request.Token, request.Password, cancellationToken);

            // No session is issued here. The account signs in through the
            // ordinary login, which is also what proves the password it just set
            // is the one it thinks it set.
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("auth-control-plane");

        // Public self-service sign-up (docs/self-service-saas-plan.md §11.1,
        // phase B). Rate-limited on the same policy as login: it is a
        // credential-adjacent surface, and it must not become a way to probe or
        // enumerate accounts. The answer is deliberately the same whether or not
        // the email was already taken.
        group.MapPost("/register", async (
            RegisterRequest request,
            IOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            await onboarding.RegisterAsync(request.Email, request.Password, request.Name, request.CompanyName, cancellationToken);
            return Results.Accepted(value: new RegistrationAcceptedResponse { Status = "verification_required" });
        }).AllowAnonymous().RequireRateLimiting("auth-control-plane");

        group.MapPost("/verify-email", async (
            VerifyEmailRequest request,
            IOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            var verified = await onboarding.VerifyEmailAsync(request.Token, cancellationToken);

            // A bad or expired token is a bad token — not a hint about any email.
            return verified
                ? Results.Ok(new RegistrationAcceptedResponse { Status = "verified" })
                : Results.Problem(
                    title: "The verification link is not valid or has expired.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "invalid_verification_token" });
        }).AllowAnonymous().RequireRateLimiting("auth-control-plane");

        group.MapPost("/resend-verification", async (
            ResendVerificationRequest request,
            IOnboardingService onboarding,
            CancellationToken cancellationToken) =>
        {
            await onboarding.ResendVerificationAsync(request.Email, cancellationToken);
            return Results.Accepted(value: new RegistrationAcceptedResponse { Status = "verification_required" });
        }).AllowAnonymous().RequireRateLimiting("auth-control-plane");

        group.MapGet("/me", async (
            IControlPlanePrincipal principal,
            IControlPlaneAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.UserId is not { } userId)
            {
                return Results.Unauthorized();
            }

            var described = await service.DescribeAsync(userId, principal.SessionId, cancellationToken);
            return described is null ? Results.Unauthorized() : Results.Ok(ToResponse(described));
        }).RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy);

        // Enrolment deliberately needs no permission: an account whose second
        // factor is still outstanding holds none until it finishes here.
        group.MapPost("/mfa/enroll", async (
            IControlPlanePrincipal principal,
            IControlPlaneAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.UserId is not { } userId)
            {
                return Results.Unauthorized();
            }

            var enrollment = await service.BeginMfaEnrollmentAsync(userId, cancellationToken);
            return Results.Ok(new MfaEnrollmentResponse
            {
                Secret = enrollment.Secret,
                EnrollmentUri = enrollment.EnrollmentUri,
            });
        }).RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy);

        group.MapPost("/mfa/confirm", async (
            MfaCodeRequest request,
            HttpContext http,
            IControlPlanePrincipal principal,
            IControlPlaneAuthenticationService service,
            CancellationToken cancellationToken) =>
        {
            if (principal.UserId is not { } userId || principal.SessionId is not { } sessionId)
            {
                return Results.Unauthorized();
            }

            var result = await service.ConfirmMfaAsync(userId, sessionId, request.Code, cancellationToken);

            return result.Outcome is AuthenticationOutcome.Succeeded
                ? WriteSession(http, result)
                : Results.Problem(
                    title: "The code is not valid.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "unauthorized" });
        }).RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy);
    }

    private static IResult WriteSession(HttpContext http, AuthenticationResult result)
    {
        if (result.RefreshToken is not null)
        {
            http.Response.Cookies.Append(RefreshCookieName, result.RefreshToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = result.RefreshTokenExpiresAt,
                Path = "/api/v1/auth",
            });
        }

        return Results.Ok(new LoginResponse
        {
            Status = result.Outcome is AuthenticationOutcome.MfaEnrollmentRequired ? "mfa_enrollment_required" : "succeeded",
            AccessToken = result.AccessToken,
            ExpiresAt = result.AccessTokenExpiresAt,
            RefreshToken = result.RefreshToken,
            User = result.Principal is null ? null : ToResponse(result.Principal),
        });
    }

    private static AuthenticatedUserResponse ToResponse(AuthenticatedPrincipal principal) => new()
    {
        Id = principal.UserId,
        Email = principal.Email,
        DisplayName = principal.DisplayName,
        CustomerId = principal.CustomerId,
        Roles = principal.Roles,
        Permissions = principal.Permissions,
        MfaEnabled = principal.MfaEnabled,
        MfaSatisfied = principal.MfaSatisfied,
    };
}
