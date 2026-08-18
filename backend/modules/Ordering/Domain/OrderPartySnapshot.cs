using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Ordering.Domain;

/// <summary>
/// An immutable historical party/contact snapshot frozen at order placement time.
/// Owned strictly by Ordering; does not participate in foreign keys to the Customer module.
/// </summary>
public sealed class OrderPartySnapshot : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid? SourceCustomerId { get; private set; }

    public string DisplayName { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private OrderPartySnapshot()
    {
        DisplayName = string.Empty;
    }

    private OrderPartySnapshot(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        Guid orderId,
        Guid? sourceCustomerId,
        string displayName,
        string? phone,
        string? email)
        : base(id)
    {
        TenantId = tenantId;
        OrderId = orderId;
        SourceCustomerId = sourceCustomerId;
        DisplayName = displayName;
        Phone = phone;
        Email = email;
        CreatedAt = createdAt;
    }

    public static OrderPartySnapshot CreateFromCustomer(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        Guid orderId,
        Guid customerId,
        string displayName,
        string? phone,
        string? email)
    {
        ValidateCommon(tenantId, orderId, displayName);

        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("Source customer ID cannot be empty.");
        }

        return new OrderPartySnapshot(
            id,
            now,
            tenantId,
            orderId,
            customerId,
            displayName.Trim(),
            string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            string.IsNullOrWhiteSpace(email) ? null : email.Trim());
    }

    public static OrderPartySnapshot CreateFromGuest(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        Guid orderId,
        string displayName,
        string? phone,
        string? email)
    {
        ValidateCommon(tenantId, orderId, displayName);

        return new OrderPartySnapshot(
            id,
            now,
            tenantId,
            orderId,
            sourceCustomerId: null,
            displayName.Trim(),
            string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            string.IsNullOrWhiteSpace(email) ? null : email.Trim());
    }

    private static void ValidateCommon(Guid tenantId, Guid orderId, string displayName)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Tenant ID is required for an order party snapshot.");
        }

        if (orderId == Guid.Empty)
        {
            throw DomainException.Validation("Order ID is required for an order party snapshot.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw DomainException.Validation("Display name is required for an order party snapshot.");
        }
    }
}
