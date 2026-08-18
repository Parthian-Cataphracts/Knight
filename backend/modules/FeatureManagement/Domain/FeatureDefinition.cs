using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace FeatureManagement.Domain;

/// <summary>
/// A capability that can be enabled or disabled per tenant. The <see cref="Key"/>
/// is the stable identifier modules and tenants reference; <see cref="Name"/> and
/// <see cref="Description"/> are for administrative display only.
/// </summary>
public sealed class FeatureDefinition : AuditableEntity
{
    public string Key { get; private set; }

    public string Name { get; private set; }

    public string Description { get; private set; }

    private FeatureDefinition()
    {
        Key = string.Empty;
        Name = string.Empty;
        Description = string.Empty;
    }

    private FeatureDefinition(Guid id, DateTimeOffset createdAt, string key, string name, string description)
        : base(id, createdAt)
    {
        Key = key;
        Name = name;
        Description = description;
    }

    public static FeatureDefinition Create(Guid id, DateTimeOffset createdAt, string key, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw DomainException.Validation("Feature key is required.");
        }

        return new FeatureDefinition(id, createdAt, key.Trim().ToLowerInvariant(), name.Trim(), description.Trim());
    }
}
