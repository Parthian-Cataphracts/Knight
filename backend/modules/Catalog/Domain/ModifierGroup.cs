using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Catalog.Domain;

/// <summary>
/// A tenant-scoped set of selection rules over a collection of
/// <see cref="Modifier"/> options, assignable to products through
/// <see cref="ProductModifierGroup"/>.
/// </summary>
public sealed class ModifierGroup : AuditableEntity, ITenantScoped
{
    private const int MaxNameLength = 150;

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public bool IsRequired { get; private set; }

    public int MinSelections { get; private set; }

    public int MaxSelections { get; private set; }

    public int DisplayOrder { get; private set; }

    private ModifierGroup()
    {
        Name = string.Empty;
    }

    private ModifierGroup(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        string name,
        bool isRequired,
        int minSelections,
        int maxSelections,
        int displayOrder)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        Name = name;
        IsRequired = isRequired;
        MinSelections = minSelections;
        MaxSelections = maxSelections;
        DisplayOrder = displayOrder;
    }

    public static ModifierGroup Create(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        string name,
        bool isRequired,
        int minSelections,
        int maxSelections,
        int displayOrder)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("A modifier group must belong to a tenant.");
        }

        ValidateSelectionRules(isRequired, minSelections, maxSelections);

        return new ModifierGroup(
            id,
            now,
            tenantId,
            ValidateName(name),
            isRequired,
            minSelections,
            maxSelections,
            displayOrder);
    }

    public void Rename(string name, DateTimeOffset now)
    {
        Name = ValidateName(name);
        MarkUpdated(now);
    }

    public void UpdateSelectionRules(bool isRequired, int min, int max, DateTimeOffset now)
    {
        ValidateSelectionRules(isRequired, min, max);

        IsRequired = isRequired;
        MinSelections = min;
        MaxSelections = max;
        MarkUpdated(now);
    }

    public void UpdateDetails(
        string name,
        bool isRequired,
        int minSelections,
        int maxSelections,
        int displayOrder,
        DateTimeOffset now)
    {
        ValidateSelectionRules(isRequired, minSelections, maxSelections);

        Name = ValidateName(name);
        IsRequired = isRequired;
        MinSelections = minSelections;
        MaxSelections = maxSelections;
        DisplayOrder = displayOrder;
        MarkUpdated(now);
    }

    private static void ValidateSelectionRules(bool isRequired, int min, int max)
    {
        if (min < 0)
        {
            throw DomainException.Validation("Modifier group minimum selections cannot be negative.");
        }

        if (max < min)
        {
            throw DomainException.Validation("Modifier group maximum selections cannot be less than the minimum selections.");
        }

        if (isRequired && min < 1)
        {
            throw DomainException.Validation("A required modifier group must allow at least one minimum selection.");
        }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Modifier group name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Modifier group name cannot exceed {MaxNameLength} characters.");
        }

        return trimmed;
    }
}
