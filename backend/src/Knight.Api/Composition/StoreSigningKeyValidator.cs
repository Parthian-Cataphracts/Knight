using Microsoft.Extensions.Options;
using Stores;

namespace Knight.Api.Composition;

/// <summary>
/// Refuses to start outside Development without a store payload-signing key.
///
/// Without one, <c>StorePayloadSigner</c> derives store keys from the JWT
/// signing key. That is a reasonable convenience on a laptop and a bad idea
/// anywhere else: one key would then sign both the tokens that authenticate
/// dashboard users and the entitlement payloads stores cache and trust, so a
/// single leak would compromise both. Fail at startup with a sentence, rather
/// than run for a year in that state (docs/authentication.md §5).
/// </summary>
public sealed class StoreSigningKeyValidator : IValidateOptions<StoreOptions>
{
    private readonly IHostEnvironment _environment;

    public StoreSigningKeyValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, StoreOptions options)
    {
        if (_environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.IntegrationSigningKey))
        {
            return ValidateOptionsResult.Fail(
                "Stores:IntegrationSigningKey is required outside Development. Supply it via environment variable " +
                "or a secret store; it must not be the JWT signing key.");
        }

        return ValidateOptionsResult.Success;
    }
}
