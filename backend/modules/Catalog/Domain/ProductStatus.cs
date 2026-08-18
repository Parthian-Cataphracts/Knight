namespace Catalog.Domain;

/// <summary>Lifecycle state of a <see cref="Product"/>. Persisted as its string name.</summary>
public enum ProductStatus
{
    Draft = 0,
    Active = 1,
    Archived = 2
}
