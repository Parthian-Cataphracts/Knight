using Microsoft.Extensions.Options;
using Knight.Infrastructure.Security;

namespace Knight.Api.Composition;

/// <summary>
/// Refuses to start in Production with a development-placeholder or otherwise
/// obviously unsafe signing key. <c>JwtOptions</c>'s DataAnnotations only check
/// length; this catches the specific known placeholder value shipped in
/// appsettings.Development.json so it can never be mistaken for a real secret.
/// </summary>
public sealed class JwtProductionSigningKeyValidator : IValidateOptions<JwtOptions>
{
    private const string KnownDevelopmentPlaceholder = "development-only-signing-key-change-me-please-32chars+";

    private readonly IHostEnvironment _environment;

    public JwtProductionSigningKeyValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        if (!_environment.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        if (string.Equals(options.SigningKey, KnownDevelopmentPlaceholder, StringComparison.Ordinal) ||
            options.SigningKey.Contains("development", StringComparison.OrdinalIgnoreCase) ||
            options.SigningKey.Contains("change-me", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "Jwt:SigningKey is set to a development placeholder value. Production requires a real secret " +
                "supplied via environment variable or a secret store — see docs/security/README.md.");
        }

        return ValidateOptionsResult.Success;
    }
}
