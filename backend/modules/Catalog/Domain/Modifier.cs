using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Catalog.Domain;

/// <summary>
/// A single selectable option within a <see cref="ModifierGroup"/>.
/// </summary>
public sealed class Modifier : AuditableEntity, ITenantScoped
{
    private const int MaxNameLength = 150;

    public Guid TenantId { get; private set; }

    public Guid ModifierGroupId { get; private set; }

    public string Name { get; private set; }

    public decimal PriceDelta { get; private set; }

    public bool IsAvailable { get; private set; }

    public int DisplayOrder { get; private set; }

    private Modifier()
    {
        Name = string.Empty;
    }

    private Modifier(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        Guid modifierGroupId,
        string name,
        decimal priceDelta,
        bool isAvailable,
        int displayOrder)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        ModifierGroupId = modifierGroupId;
        Name = name;
        PriceDelta = priceDelta;
        IsAvailable = isAvailable;
        DisplayOrder = displayOrder;
    }

    public static Modifier Create(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        Guid modifierGroupId,
        string name,
        decimal priceDelta,
        bool isAvailable,
        int displayOrder)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("A modifier must belong to a tenant.");
        }

        if (modifierGroupId == Guid.Empty)
        {
            throw DomainException.Validation("A modifier must belong to a modifier group.");
        }

        return new Modifier(
            id,
            now,
            tenantId,
            modifierGroupId,
            ValidateName(name),
            ValidatePriceDelta(priceDelta),
            isAvailable,
            displayOrder);
    }

    public void UpdateDetails(
        string name,
        decimal priceDelta,
        bool isAvailable,
        int displayOrder,
        DateTimeOffset now)
    {
        Name = ValidateName(name);
        PriceDelta = ValidatePriceDelta(priceDelta);
        IsAvailable = isAvailable;
        DisplayOrder = displayOrder;
        MarkUpdated(now);
    }

    public void SetAvailability(bool isAvailable, DateTimeOffset now)
    {
        IsAvailable = isAvailable;
        MarkUpdated(now);
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Modifier name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Modifier name cannot exceed {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static decimal ValidatePriceDelta(decimal priceDelta)
    {
        if (priceDelta < 0m)
        {
            throw DomainException.Validation("Modifier price delta cannot be negative.");
        }

        return priceDelta;
    }
}
