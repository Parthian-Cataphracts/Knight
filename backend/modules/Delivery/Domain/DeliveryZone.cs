using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Delivery.Domain;

public sealed class DeliveryZone : AuditableEntity, ITenantScoped
{
    private const int MaxNameLength = 100;

    public Guid TenantId { get; private set; }
    public string Name { get; private set; }
    public decimal Fee { get; private set; }
    public decimal? MinimumOrderSubtotal { get; private set; }
    public DeliveryZoneStatus Status { get; private set; }
    public int DisplayOrder { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    private DeliveryZone()
    {
        Name = string.Empty;
    }

    private DeliveryZone(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        string name,
        decimal fee,
        decimal? minimumOrderSubtotal,
        DeliveryZoneStatus status,
        int displayOrder,
        DateTimeOffset? updatedAt,
        DateTimeOffset? archivedAt)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        Name = name;
        Fee = fee;
        MinimumOrderSubtotal = minimumOrderSubtotal;
        Status = status;
        DisplayOrder = displayOrder;
        UpdatedAt = updatedAt;
        ArchivedAt = archivedAt;
    }

    public static DeliveryZone Create(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        string name,
        decimal fee,
        decimal? minimumOrderSubtotal = null,
        int displayOrder = 0)
    {
        if (id == Guid.Empty)
        {
            throw DomainException.Validation("Delivery zone ID cannot be empty.");
        }

        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Delivery zone must belong to a tenant.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Delivery zone name is required.");
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Delivery zone name cannot exceed {MaxNameLength} characters.");
        }

        if (fee < 0)
        {
            throw DomainException.Validation("Delivery zone fee cannot be negative.");
        }

        if (minimumOrderSubtotal.HasValue && minimumOrderSubtotal.Value < 0)
        {
            throw DomainException.Validation("Minimum order subtotal cannot be negative.");
        }

        return new DeliveryZone(
            id,
            now,
            tenantId,
            trimmedName,
            fee,
            minimumOrderSubtotal,
            DeliveryZoneStatus.Active,
            displayOrder,
            null,
            null);
    }

    public void Update(
        string name,
        decimal fee,
        decimal? minimumOrderSubtotal,
        int displayOrder,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Delivery zone name is required.");
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > MaxNameLength)
        {
            throw DomainException.Validation($"Delivery zone name cannot exceed {MaxNameLength} characters.");
        }

        if (fee < 0)
        {
            throw DomainException.Validation("Delivery zone fee cannot be negative.");
        }

        if (minimumOrderSubtotal.HasValue && minimumOrderSubtotal.Value < 0)
        {
            throw DomainException.Validation("Minimum order subtotal cannot be negative.");
        }

        Name = trimmedName;
        Fee = fee;
        MinimumOrderSubtotal = minimumOrderSubtotal;
        DisplayOrder = displayOrder;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        if (Status == DeliveryZoneStatus.Archived)
        {
            throw DomainException.Conflict("Delivery zone is already archived.");
        }

        Status = DeliveryZoneStatus.Archived;
        ArchivedAt = now;
        UpdatedAt = now;
    }

    public void Restore(DateTimeOffset now)
    {
        if (Status == DeliveryZoneStatus.Active)
        {
            throw DomainException.Conflict("Delivery zone is already active.");
        }

        Status = DeliveryZoneStatus.Active;
        ArchivedAt = null;
        UpdatedAt = now;
    }
}
