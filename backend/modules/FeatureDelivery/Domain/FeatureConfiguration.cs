using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace FeatureDelivery.Domain;

/// <summary>
/// The customer-specific settings one Feature runs with in one store
/// (docs/feature-delivery.md §9).
///
/// Configuration lives here and never in the package. A Feature is implemented
/// once and delivered to everyone, so the moment a customer's value is inside
/// the artifact, the artifact stops being the same artifact for everyone and the
/// whole delivery model unravels.
///
/// Non-secret values and secret values are stored in separate columns, not in one
/// document with a flag. It is the separation that makes "never return a secret
/// from a read API" enforceable by construction rather than by every read path
/// remembering to filter — the read model simply does not have the column.
/// </summary>
public sealed class FeatureConfiguration : AuditableEntity, ICustomerOwned
{
    public Guid StoreId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid FeatureId { get; private set; }

    /// <summary>The plain values, as a JSON document validated against the manifest's schema.</summary>
    public string ValuesJson { get; private set; }

    /// <summary>
    /// The secret values, encrypted at rest as a single sealed document. Null
    /// when the Feature declares no secrets.
    /// </summary>
    public string? EncryptedSecretsJson { get; private set; }

    /// <summary>
    /// The names of the secrets that are set, kept in the clear so the dashboard
    /// can show "analytics_api_key: set" without anything being able to read the
    /// value.
    /// </summary>
    public string SecretNamesJson { get; private set; }

    /// <summary>
    /// Incremented on every change. The store echoes back the version it applied,
    /// which is how drift between what KNIGHT sent and what the store is running
    /// becomes detectable rather than assumed.
    /// </summary>
    public int Version { get; private set; }

    public Guid UpdatedBy { get; private set; }

    /// <summary>The configuration version the store last confirmed it applied.</summary>
    public int? AppliedVersion { get; private set; }

    public DateTimeOffset? AppliedAt { get; private set; }

    private FeatureConfiguration()
    {
        ValuesJson = "{}";
        SecretNamesJson = "[]";
    }

    private FeatureConfiguration(
        Guid id,
        DateTimeOffset createdAt,
        Guid storeId,
        Guid customerId,
        Guid featureId,
        string valuesJson,
        string? encryptedSecretsJson,
        string secretNamesJson,
        Guid updatedBy)
        : base(id, createdAt)
    {
        StoreId = storeId;
        CustomerId = customerId;
        FeatureId = featureId;
        ValuesJson = valuesJson;
        EncryptedSecretsJson = encryptedSecretsJson;
        SecretNamesJson = secretNamesJson;
        UpdatedBy = updatedBy;
        Version = 1;
    }

    public static FeatureConfiguration Create(
        Guid id,
        DateTimeOffset createdAt,
        Guid storeId,
        Guid customerId,
        Guid featureId,
        string valuesJson,
        string? encryptedSecretsJson,
        string secretNamesJson,
        Guid updatedBy)
    {
        if (storeId == Guid.Empty || customerId == Guid.Empty || featureId == Guid.Empty)
        {
            throw DomainException.Validation("A configuration must name its store, customer and feature.");
        }

        return new FeatureConfiguration(
            id,
            createdAt,
            storeId,
            customerId,
            featureId,
            RequireJson(valuesJson, "configuration values"),
            encryptedSecretsJson,
            RequireJson(secretNamesJson, "secret names"),
            updatedBy);
    }

    /// <summary>
    /// Replaces the configuration. The version moves forward on every change,
    /// including one that sets the same values again: the store needs to be told
    /// to re-apply, and a version that did not change would look to it like
    /// nothing had happened.
    /// </summary>
    public void Replace(
        string valuesJson,
        string? encryptedSecretsJson,
        string secretNamesJson,
        Guid updatedBy,
        DateTimeOffset now)
    {
        ValuesJson = RequireJson(valuesJson, "configuration values");
        EncryptedSecretsJson = encryptedSecretsJson;
        SecretNamesJson = RequireJson(secretNamesJson, "secret names");
        UpdatedBy = updatedBy;
        Version++;
        MarkUpdated(now);
    }

    /// <summary>
    /// Records that the store confirmed it applied a version.
    ///
    /// A confirmation for an older version than the current one is kept rather
    /// than discarded: it is exactly the evidence that the store is behind, which
    /// is what <see cref="HasDrifted"/> reports and what the reconciliation job
    /// acts on.
    /// </summary>
    public void RecordApplied(int appliedVersion, DateTimeOffset now)
    {
        if (appliedVersion < 1)
        {
            throw DomainException.Validation("An applied configuration version must be positive.");
        }

        if (appliedVersion > Version)
        {
            throw DomainException.Conflict(
                $"The store reported configuration version {appliedVersion}, which KNIGHT never issued.");
        }

        AppliedVersion = appliedVersion;
        AppliedAt = now;
        MarkUpdated(now);
    }

    /// <summary>True when the store is not running the configuration KNIGHT holds.</summary>
    public bool HasDrifted => AppliedVersion is null || AppliedVersion != Version;

    private static string RequireJson(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw DomainException.Validation($"The {what} document is required.");
        }

        return value.Trim();
    }
}
