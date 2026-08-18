using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stores;

/// <summary>
/// Bound from configuration (section "Stores").
/// </summary>
public sealed class StoreOptions
{
    public const string SectionName = "Stores";

    /// <summary>
    /// How long a rotated credential keeps working. Long enough for a store to
    /// pick the new secret up on its next configuration reload, short enough that
    /// a compromised secret is not usable for a working day (risks.md R8).
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan RotationGracePeriod { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Optional absolute lifetime for newly issued credentials; null means they expire only when rotated or revoked.</summary>
    public TimeSpan? CredentialLifetime { get; init; }
}

public static class StoresModule
{
    public static IServiceCollection AddStoresModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StoreOptions>()
            .Bind(configuration.GetSection(StoreOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IStoreManagementService, StoreManagementService>();

        return services;
    }
}
