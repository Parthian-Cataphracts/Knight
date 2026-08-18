using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Plans.Domain;

/// <summary>
/// A commercial plan: a base price plus the set of features it includes.
///
/// Plans are data. Their contents are seeded from a file and edited through the
/// dashboard, never hard-coded into a service or a React component
/// (docs/domain-model.md section 4). Nothing in this codebase may branch on
/// <c>plan.Key == "professional"</c> to decide what a customer gets; it must ask
/// what the plan actually includes.
/// </summary>
public sealed class Plan : AuditableEntity
{
    public string Key { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public decimal BasePriceAmount { get; private set; }

    public string Currency { get; private set; }

    public bool IsActive { get; private set; }

    public int SortOrder { get; private set; }

    private readonly List<PlanFeature> _features = [];

    public IReadOnlyCollection<PlanFeature> Features => _features.AsReadOnly();

    public Money BasePrice => Money.Of(BasePriceAmount, Currency);

    private Plan()
    {
        Key = string.Empty;
        Name = string.Empty;
        Currency = string.Empty;
    }

    private Plan(Guid id, DateTimeOffset createdAt, string key, string name, Money basePrice, int sortOrder)
        : base(id, createdAt)
    {
        Key = key;
        Name = name;
        BasePriceAmount = basePrice.Amount;
        Currency = basePrice.Currency;
        SortOrder = sortOrder;
        IsActive = true;
    }

    public static Plan Create(Guid id, DateTimeOffset createdAt, string key, string name, Money basePrice, int sortOrder = 0)
        => new(id, createdAt, ValidateKey(key), ValidateName(name), basePrice, sortOrder);

    public void UpdateMetadata(string name, string? description, int sortOrder, DateTimeOffset now)
    {
        Name = ValidateName(name);
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        SortOrder = sortOrder;
        MarkUpdated(now);
    }

    /// <summary>
    /// Repricing affects future invoices only. Issued invoices are historical
    /// records of what was charged and are never recalculated from current
    /// prices.
    /// </summary>
    public void Reprice(Money basePrice, DateTimeOffset now)
    {
        BasePriceAmount = basePrice.Amount;
        Currency = basePrice.Currency;
        MarkUpdated(now);
    }

    public void Activate(DateTimeOffset now)
    {
        IsActive = true;
        MarkUpdated(now);
    }

    /// <summary>
    /// Deactivating stops the plan being sold. Existing subscriptions to it
    /// continue: a customer does not lose what they bought because the plan was
    /// retired from the price list.
    /// </summary>
    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        MarkUpdated(now);
    }

    /// <summary>
    /// Adds or replaces a feature entry. <paramref name="isCustomerToggleable"/>
    /// is the whole of the answer to "may the customer switch this on
    /// themselves?" — a feature that is included but not toggleable is part of
    /// the plan and cannot be turned off either.
    /// </summary>
    public PlanFeature SetFeature(
        Guid featureId,
        bool isIncluded,
        bool isCustomerToggleable,
        string? pinnedVersionRange,
        DateTimeOffset now)
    {
        if (featureId == Guid.Empty)
        {
            throw DomainException.Validation("A plan feature must name a feature.");
        }

        var existing = _features.SingleOrDefault(f => f.FeatureId == featureId);
        if (existing is not null)
        {
            existing.Update(isIncluded, isCustomerToggleable, pinnedVersionRange);
            MarkUpdated(now);
            return existing;
        }

        var entry = PlanFeature.Create(Id, featureId, isIncluded, isCustomerToggleable, pinnedVersionRange);
        _features.Add(entry);
        MarkUpdated(now);
        return entry;
    }

    public void RemoveFeature(Guid featureId, DateTimeOffset now)
    {
        var existing = _features.SingleOrDefault(f => f.FeatureId == featureId)
            ?? throw DomainException.Conflict("The plan does not list this feature.");

        _features.Remove(existing);
        MarkUpdated(now);
    }

    public PlanFeature? Find(Guid featureId) => _features.SingleOrDefault(f => f.FeatureId == featureId);

    /// <summary>Features the plan grants without the customer choosing anything.</summary>
    public IReadOnlyCollection<Guid> IncludedFeatureIds =>
        _features.Where(f => f.IsIncluded).Select(f => f.FeatureId).ToArray();

    /// <summary>Features the customer may switch on for themselves, at whatever they cost.</summary>
    public IReadOnlyCollection<Guid> SelectableFeatureIds =>
        _features.Where(f => f is { IsIncluded: false, IsCustomerToggleable: true }).Select(f => f.FeatureId).ToArray();

    private static string ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw DomainException.Validation("Plan key is required.");
        }

        var normalized = key.Trim().ToLowerInvariant();
        if (normalized.Length > 50)
        {
            throw DomainException.Validation("Plan key must be 50 characters or fewer.");
        }

        if (!normalized.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-'))
        {
            throw DomainException.Validation("Plan key must be lowercase letters, digits or hyphens.");
        }

        return normalized;
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Plan name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 200)
        {
            throw DomainException.Validation("Plan name must be 200 characters or fewer.");
        }

        return trimmed;
    }
}
