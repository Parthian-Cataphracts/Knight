using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Onboarding;

/// <summary>Bound from configuration (section "Onboarding").</summary>
public sealed class OnboardingOptions
{
    public const string SectionName = "Onboarding";

    /// <summary>
    /// How long a verification link stays usable. A day by default: long enough
    /// to survive a full inbox, short enough that a forwarded mail is not a
    /// standing way to claim an address.
    /// </summary>
    [Range(typeof(TimeSpan), "00:15:00", "7.00:00:00")]
    public TimeSpan EmailVerificationLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Minimum password length accepted at registration.</summary>
    [Range(8, 128)]
    public int MinPasswordLength { get; init; } = 10;
}

/// <summary>
/// The self-service sign-up flow (docs/self-service-saas-plan.md §12, phase B).
/// Lives in its own module because it is the one place that spans the two
/// domains a customer is made of — the customer record and the account that owns
/// it — and later phases hang checkout and provisioning off the same seam.
/// </summary>
public static class OnboardingModule
{
    public static IServiceCollection AddOnboardingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OnboardingOptions>()
            .Bind(configuration.GetSection(OnboardingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IOnboardingService, OnboardingService>();

        return services;
    }
}
