using Knight.Domain.Exceptions;

namespace Provisioning.Domain;

/// <summary>Who carries a step out.</summary>
public enum ProvisioningStepMode
{
    /// <summary>KNIGHT does it, or watches for the fact that proves it happened.</summary>
    Automatic = 0,

    /// <summary>
    /// A person does it and records that they did. Creating the VM and wiring
    /// DNS and TLS are here today (docs/store-provisioning.md §2). The step is
    /// real either way; only who completes it changes when the automation lands.
    /// </summary>
    Manual = 1,
}

public sealed record ProvisioningStepDefinition(string Name, ProvisioningStepMode Mode, string Description);

/// <summary>
/// The steps of a provisioning and a deprovisioning run, in order.
///
/// Data rather than code, for the same reason the delivery pipeline is: the
/// dashboard, the coordinator and the audit trail all name these steps, and a
/// step nobody declared must not be reportable.
/// </summary>
public static class ProvisioningPipeline
{
    // Provisioning.
    public const string Server = "server";
    public const string Instance = "instance";
    public const string StoreRecord = "store-record";
    public const string Credentials = "credentials";
    public const string Agent = "agent";
    public const string BaseFeatures = "base-features";
    public const string Configuration = "configuration";
    public const string DomainAndTls = "domain-tls";
    public const string HealthCheck = "healthcheck";

    // Deprovisioning.
    public const string DisableFeatures = "disable-features";
    public const string RevokeAccess = "revoke-access";
    public const string StopIngestion = "stop-ingestion";
    public const string Retain = "retain";
    public const string Export = "export";
    public const string Purge = "purge";

    private static readonly ProvisioningStepDefinition[] ProvisionSteps =
    [
        new(Server, ProvisioningStepMode.Manual, "The machine exists and is recorded against the store."),
        new(Instance, ProvisioningStepMode.Manual, "A Django instance, database and Redis built from the base store image."),
        new(StoreRecord, ProvisioningStepMode.Automatic, "The store is registered with KNIGHT."),
        new(Credentials, ProvisioningStepMode.Automatic, "Store credentials issued."),
        new(Agent, ProvisioningStepMode.Automatic, "An agent on the store's server has enrolled."),
        new(BaseFeatures, ProvisioningStepMode.Automatic, "The plan's base Features are installed."),
        new(Configuration, ProvisioningStepMode.Automatic, "The store has handshaked and holds its configuration."),
        new(DomainAndTls, ProvisioningStepMode.Manual, "The primary domain resolves to the store and serves valid TLS."),
        new(HealthCheck, ProvisioningStepMode.Automatic, "The store reports healthy; only then does it become Active."),
    ];

    private static readonly ProvisioningStepDefinition[] DeprovisionSteps =
    [
        new(DisableFeatures, ProvisioningStepMode.Automatic, "Every installed Feature is disabled."),
        new(RevokeAccess, ProvisioningStepMode.Automatic, "Store credentials and the agent's token are revoked."),
        new(StopIngestion, ProvisioningStepMode.Automatic, "The store can no longer report anything to KNIGHT."),
        new(Retain, ProvisioningStepMode.Automatic, "The contractual retention window runs."),
        new(Export, ProvisioningStepMode.Automatic, "The customer's export is produced before anything is purged."),
        new(Purge, ProvisioningStepMode.Automatic, "The store's retained data is deleted."),
    ];

    public static IReadOnlyList<ProvisioningStepDefinition> StepsFor(ProvisioningKind kind) => kind switch
    {
        ProvisioningKind.Provision => ProvisionSteps,
        ProvisioningKind.Deprovision => DeprovisionSteps,
        _ => throw DomainException.Validation($"'{kind}' is not a known provisioning kind."),
    };

    /// <summary>Returns the step definition, or refuses a name that is not part of this pipeline.</summary>
    public static ProvisioningStepDefinition Require(ProvisioningKind kind, string stepName)
    {
        if (string.IsNullOrWhiteSpace(stepName))
        {
            throw DomainException.Validation("A step name is required.");
        }

        var name = stepName.Trim();

        return StepsFor(kind).FirstOrDefault(step => string.Equals(step.Name, name, StringComparison.Ordinal))
            ?? throw DomainException.Validation($"'{name}' is not a step of a {kind} job.");
    }
}
