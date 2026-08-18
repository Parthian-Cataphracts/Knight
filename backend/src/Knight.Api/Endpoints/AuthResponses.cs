using Identity.Authentication;
using Microsoft.AspNetCore.Mvc;
using Knight.Contracts.Auth;

namespace Knight.Api.Endpoints;

/// <summary>
/// Shared response shaping for Platform and Tenant auth endpoints — keeps both
/// route groups returning byte-for-byte the same generic failure shape so
/// neither leaks which specific internal reason (unknown email, wrong
/// password, locked, disabled) caused an authentication failure. See
/// docs/architecture/authorization.md ("login enumeration resistance").
/// </summary>
internal static class AuthResponses
{
    public static IResult GenericUnauthorized() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Invalid authentication credentials.",
        type: "https://httpstatuses.io/401");

    public static LoginResponse ToLoginResponse(IssuedSession session) => new()
    {
        AccessToken = session.AccessToken,
        TokenType = "Bearer",
        ExpiresInSeconds = (int)(session.AccessTokenExpiresAt - DateTimeOffset.UtcNow).TotalSeconds
    };

    public static IResult ToChangePasswordFailure(ChangePasswordOutcome outcome) => outcome switch
    {
        ChangePasswordOutcome.InvalidCurrentPassword => Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Current password is incorrect.",
            type: "https://httpstatuses.io/401"),

        ChangePasswordOutcome.PasswordPolicyViolation => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["newPassword"] = ["The new password does not meet the platform's password policy."]
        }),

        _ => Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Account is not available.",
            type: "https://httpstatuses.io/401")
    };
}
