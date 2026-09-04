using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Provisioning;

/// <summary>
/// Bound from configuration (section "Provisioning").
/// </summary>
public sealed class ProvisioningOptions
{
    public const string SectionName = "Provisioning";

    /// <summary>
    /// How often the coordinator re-evaluates jobs that are waiting.
    ///
    /// Provisioning waits on things that take minutes — an agent enrolling, a
    /// migration running, DNS propagating — so this is a patient loop, not a
    /// tight one. Nothing is lost by being a minute late; a store that came up
    /// is still up.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:15", "00:30:00")]
    public TimeSpan CoordinatorInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How many jobs one sweep looks at.</summary>
    [Range(1, 500)]
    public int CoordinatorBatchSize { get; init; } = 50;

    /// <summary>
    /// How long a deprovisioned store's data is kept when neither the customer
    /// nor their plan says otherwise. Thirty days is the conservative end: data
    /// deleted early cannot be handed back, and a customer who leaves in anger
    /// often asks for an export a fortnight later.
    /// </summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "3650.00:00:00")]
    public TimeSpan DefaultRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// When true, an automated infrastructure adapter produces the machine, agent,
    /// credential, domain and handshake facts a provisioning run waits on — so a
    /// self-service store comes up with no operator and no real cloud. This is the
    /// simulated adapter the self-service plan calls for until a real hosting
    /// provider is chosen (docs/self-service-saas-plan.md §11). Off by default: a
    /// real deployment must not fabricate infrastructure it does not have.
    /// </summary>
    public bool SimulateInfrastructure { get; init; }
}

public static class ProvisioningModule
{
    public static IServiceCollection AddProvisioningModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProvisioningOptions>()
            .Bind(configuration.GetSection(ProvisioningOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IProvisioningService, ProvisioningService>();

        return services;
    }
}
