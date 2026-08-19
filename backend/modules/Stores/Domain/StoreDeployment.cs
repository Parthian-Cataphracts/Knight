using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Stores.Domain;

/// <summary>
/// A version of a store going live (docs/domain-model.md section 2).
///
/// KNIGHT learns about a deployment in one of two ways, and both are recorded
/// the same way so the history reads as one sequence: the store announces it,
/// or the store simply starts reporting a version KNIGHT had not seen. The
/// second is not a lesser fact — a store that deploys without telling anyone is
/// exactly the case the history exists to make visible.
/// </summary>
public sealed class StoreDeployment : Entity, ICustomerOwned
{
    public Guid StoreId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string Version { get; private set; }

    /// <summary>What the store was running before; null for the first version KNIGHT ever saw.</summary>
    public string? PreviousVersion { get; private set; }

    public DateTimeOffset DeployedAt { get; private set; }

    public DateTimeOffset DetectedAt { get; private set; }

    public StoreDeploymentSource Source { get; private set; }

    public StoreDeploymentStatus Status { get; private set; }

    /// <summary>Free text from the store's own report; absent for a detected deployment.</summary>
    public string? Notes { get; private set; }

    private StoreDeployment()
    {
        Version = string.Empty;
    }

    private StoreDeployment(
        Guid id,
        Guid storeId,
        Guid customerId,
        string version,
        string? previousVersion,
        DateTimeOffset deployedAt,
        DateTimeOffset detectedAt,
        StoreDeploymentSource source,
        StoreDeploymentStatus status,
        string? notes)
        : base(id)
    {
        StoreId = storeId;
        CustomerId = customerId;
        Version = version;
        PreviousVersion = previousVersion;
        DeployedAt = deployedAt;
        DetectedAt = detectedAt;
        Source = source;
        Status = status;
        Notes = notes;
    }

    /// <summary>Records a version KNIGHT observed rather than one anybody announced.</summary>
    public static StoreDeployment Detected(
        Guid id,
        Guid storeId,
        Guid customerId,
        string version,
        string? previousVersion,
        DateTimeOffset detectedAt) =>
        new(
            id,
            storeId,
            customerId,
            RequireVersion(version),
            StoreNormalization.NormalizeVersion(previousVersion),
            detectedAt,
            detectedAt,
            StoreDeploymentSource.VersionChange,
            StoreDeploymentStatus.Detected,
            null);

    /// <summary>Records a deployment the store reported, including a failed one.</summary>
    public static StoreDeployment Reported(
        Guid id,
        Guid storeId,
        Guid customerId,
        string version,
        string? previousVersion,
        DateTimeOffset deployedAt,
        DateTimeOffset detectedAt,
        StoreDeploymentStatus status,
        string? notes)
    {
        if (status is StoreDeploymentStatus.Detected)
        {
            throw DomainException.Validation("A reported deployment must say how it went.");
        }

        return new StoreDeployment(
            id,
            storeId,
            customerId,
            RequireVersion(version),
            StoreNormalization.NormalizeVersion(previousVersion),
            deployedAt,
            detectedAt,
            StoreDeploymentSource.StoreReported,
            status,
            string.IsNullOrWhiteSpace(notes) ? null : notes.Trim());
    }

    /// <summary>
    /// Upgrades a deployment KNIGHT merely observed into one the store
    /// confirmed.
    ///
    /// Both facts arrive for the same deployment whenever a store reports one:
    /// the version it announces is also the version it is now running, so
    /// detection sees it too. They are one deployment, and recording two would
    /// make the history read as a redeploy that never happened.
    /// </summary>
    public void Confirm(StoreDeploymentStatus status, DateTimeOffset deployedAt, string? notes)
    {
        if (Status is not StoreDeploymentStatus.Detected)
        {
            throw DomainException.Conflict("Only a detected deployment can be confirmed.");
        }

        if (status is StoreDeploymentStatus.Detected)
        {
            throw DomainException.Validation("A confirmation must say how the deployment went.");
        }

        Status = status;
        Source = StoreDeploymentSource.StoreReported;
        DeployedAt = deployedAt;
        Notes = string.IsNullOrWhiteSpace(notes) ? Notes : notes.Trim();
    }

    private static string RequireVersion(string version) =>
        StoreNormalization.NormalizeVersion(version)
        ?? throw DomainException.Validation("A deployment must name a version.");
}
