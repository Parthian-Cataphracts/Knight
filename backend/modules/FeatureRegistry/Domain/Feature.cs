using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace FeatureRegistry.Domain;

/// <summary>
/// A Feature's identity: what the capability is called, what it costs to run,
/// and whether a customer may switch it on for themselves.
///
/// A Feature is versioned, deployable Django functionality, never a boolean flag
/// (docs/adr/0014-features-as-deployable-packages.md). This type carries only
/// the identity and the commercial metadata that plans and entitlements need;
/// the versions, manifests, artifacts and dependencies that make it deployable
/// arrive with the registry proper in phase 3.5.
///
/// The distinction that matters here is the one the whole subsystem rests on:
/// an entitlement is a commercial fact about a customer, and it is not an
/// installation. Granting one triggers delivery; it does not by itself make the
/// capability exist in a store.
/// </summary>
public sealed class Feature : AuditableEntity
{
    public string Slug { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public string Category { get; private set; }

    /// <summary>
    /// False for the capabilities every store has. An optional feature can be
    /// sold, granted and revoked; a non-optional one is simply part of the
    /// product.
    /// </summary>
    public bool IsOptional { get; private set; }

    /// <summary>
    /// True when the capability cannot run on shared hosting — it needs a
    /// machine the customer does not share. Entitling one to a customer whose
    /// stores are shared-managed is refused rather than sold and then discovered
    /// (docs/domain-model.md section 4).
    /// </summary>
    public bool RequiresDedicatedInfrastructure { get; private set; }

    public FeatureStatus Status { get; private set; }

    /// <summary>
    /// The parent this Feature is a sub-feature of, or null when it is a
    /// top-level Feature.
    ///
    /// A composed Feature — "Automatic Admin" is the first — is a parent whose
    /// price for a customer is the sum of the sub-features they chose. Each
    /// sub-feature is itself a Feature with its own price, entitlement and UI
    /// mount, so the whole of pricing, entitlement and Phase-32B gating applies
    /// to it unchanged; the only new fact is that it belongs to a parent, which
    /// is what lets the catalogue and the portal present and total the group
    /// (docs/adr/0037-composed-pricing-and-sub-features.md).
    /// </summary>
    public Guid? ParentFeatureId { get; private set; }

    /// <summary>True when this Feature is a priced part of a composed parent.</summary>
    public bool IsSubFeature => ParentFeatureId is not null;

    private Feature()
    {
        Slug = string.Empty;
        Name = string.Empty;
        Category = string.Empty;
    }

    private Feature(
        Guid id,
        DateTimeOffset createdAt,
        string slug,
        string name,
        string category,
        bool isOptional,
        bool requiresDedicatedInfrastructure)
        : base(id, createdAt)
    {
        Slug = slug;
        Name = name;
        Category = category;
        IsOptional = isOptional;
        RequiresDedicatedInfrastructure = requiresDedicatedInfrastructure;
        Status = FeatureStatus.Draft;
    }

    public static Feature Create(
        Guid id,
        DateTimeOffset createdAt,
        string slug,
        string name,
        string category,
        bool isOptional = true,
        bool requiresDedicatedInfrastructure = false)
        => new(
            id,
            createdAt,
            FeatureSlug.Normalize(slug),
            ValidateName(name),
            ValidateCategory(category),
            isOptional,
            requiresDedicatedInfrastructure);

    public void UpdateMetadata(string name, string? description, string category, DateTimeOffset now)
    {
        EnsureNotWithdrawn();

        Name = ValidateName(name);
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Category = ValidateCategory(category);
        MarkUpdated(now);
    }

    /// <summary>
    /// Whether the capability needs a dedicated machine is a property of the code
    /// itself, so it is only settable while the feature is still a draft: once it
    /// has been sold, changing the answer would silently invalidate entitlements
    /// that were legitimate when granted.
    /// </summary>
    public void SetInfrastructureRequirement(bool requiresDedicatedInfrastructure, DateTimeOffset now)
    {
        if (Status is not FeatureStatus.Draft)
        {
            throw DomainException.Conflict("The infrastructure requirement can only be changed while the feature is a draft.");
        }

        RequiresDedicatedInfrastructure = requiresDedicatedInfrastructure;
        MarkUpdated(now);
    }

    public void SetOptional(bool isOptional, DateTimeOffset now)
    {
        if (Status is not FeatureStatus.Draft)
        {
            throw DomainException.Conflict("Whether a feature is optional can only be changed while it is a draft.");
        }

        IsOptional = isOptional;
        MarkUpdated(now);
    }

    /// <summary>
    /// Makes this Feature a sub-feature of <paramref name="parentFeatureId"/>.
    ///
    /// Draft-only, like the other structural properties: once a Feature has been
    /// sold, moving it into or out of a composed group would change what a
    /// customer's selection totals to and what their entitlement composes from,
    /// silently. A Feature cannot be its own parent; that the parent exists and
    /// is not itself a sub-feature (composition is one level deep, deliberately)
    /// is checked by the service, which can read the other row.
    /// </summary>
    public void GroupUnder(Guid parentFeatureId, DateTimeOffset now)
    {
        if (Status is not FeatureStatus.Draft)
        {
            throw DomainException.Conflict("A feature can only be grouped under a parent while it is a draft.");
        }

        if (parentFeatureId == Guid.Empty)
        {
            throw DomainException.Validation("A parent feature is required.");
        }

        if (parentFeatureId == Id)
        {
            throw DomainException.Conflict("A feature cannot be a sub-feature of itself.");
        }

        ParentFeatureId = parentFeatureId;
        MarkUpdated(now);
    }

    /// <summary>Detaches this Feature from its parent, making it top-level again. Draft-only, for the same reason.</summary>
    public void Ungroup(DateTimeOffset now)
    {
        if (Status is not FeatureStatus.Draft)
        {
            throw DomainException.Conflict("A feature can only be ungrouped while it is a draft.");
        }

        ParentFeatureId = null;
        MarkUpdated(now);
    }

    // --- Lifecycle -------------------------------------------------------
    //
    // Draft --Publish--> Published --Deprecate--> Deprecated
    //   |                    |                        |
    //   +--------------------+------ Withdraw --------+--> Withdrawn (terminal)

    public void Publish(DateTimeOffset now)
    {
        if (Status is not FeatureStatus.Draft)
        {
            throw DomainException.Conflict($"A feature in status '{Status}' cannot be published.");
        }

        Status = FeatureStatus.Published;
        MarkUpdated(now);
    }

    /// <summary>
    /// Deprecated means "no new entitlements", not "stops working": existing
    /// customers keep the capability until it is withdrawn.
    /// </summary>
    public void Deprecate(DateTimeOffset now)
    {
        if (Status is not FeatureStatus.Published)
        {
            throw DomainException.Conflict($"A feature in status '{Status}' cannot be deprecated.");
        }

        Status = FeatureStatus.Deprecated;
        MarkUpdated(now);
    }

    public void Withdraw(DateTimeOffset now)
    {
        if (Status is FeatureStatus.Withdrawn)
        {
            throw DomainException.Conflict("The feature is already withdrawn.");
        }

        Status = FeatureStatus.Withdrawn;
        MarkUpdated(now);
    }

    /// <summary>True when a new entitlement may be granted for this feature.</summary>
    public bool CanBeEntitled => Status is FeatureStatus.Published;

    /// <summary>
    /// True when an existing entitlement remains valid. A deprecated feature is
    /// still owed to whoever already has it.
    /// </summary>
    public bool RemainsEntitled => Status is FeatureStatus.Published or FeatureStatus.Deprecated;

    private void EnsureNotWithdrawn()
    {
        if (Status is FeatureStatus.Withdrawn)
        {
            throw DomainException.Conflict("A withdrawn feature cannot be modified.");
        }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Feature name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 200)
        {
            throw DomainException.Validation("Feature name must be 200 characters or fewer.");
        }

        return trimmed;
    }

    private static string ValidateCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            throw DomainException.Validation("Feature category is required.");
        }

        var trimmed = category.Trim();
        if (trimmed.Length > 50)
        {
            throw DomainException.Validation("Feature category must be 50 characters or fewer.");
        }

        return trimmed;
    }
}

public enum FeatureStatus
{
    Draft = 0,
    Published = 1,
    Deprecated = 2,
    Withdrawn = 3,
}
