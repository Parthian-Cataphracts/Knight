using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Customer.Domain;

/// <summary>
/// A tenant-scoped Customer business entity representing an end customer.
/// Not an authentication principal; owns customer profile and lifecycle state.
/// </summary>
public sealed class Customer : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public string DisplayName { get; private set; }

    public string? Phone { get; private set; }

    public string? NormalizedPhone { get; private set; }

    public string? Email { get; private set; }

    public string? NormalizedEmail { get; private set; }

    public CustomerStatus Status { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    private Customer()
    {
        DisplayName = string.Empty;
    }

    private Customer(
        Guid id,
        DateTimeOffset createdAt,
        Guid tenantId,
        string displayName,
        string? phone,
        string? normalizedPhone,
        string? email,
        string? normalizedEmail,
        CustomerStatus status,
        DateTimeOffset? archivedAt)
        : base(id, createdAt)
    {
        TenantId = tenantId;
        DisplayName = displayName;
        Phone = phone;
        NormalizedPhone = normalizedPhone;
        Email = email;
        NormalizedEmail = normalizedEmail;
        Status = status;
        ArchivedAt = archivedAt;
    }

    public static Customer Create(
        Guid id,
        DateTimeOffset now,
        Guid tenantId,
        string displayName,
        string? phone = null,
        string? email = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw DomainException.Validation("Customer must belong to a tenant.");
        }

        var validName = CustomerNormalization.ValidateDisplayName(displayName);
        var (p, normPhone) = CustomerNormalization.NormalizePhone(phone);
        var (e, normEmail) = CustomerNormalization.NormalizeEmail(email);

        ValidateContactRequirement(normPhone, normEmail);

        return new Customer(
            id,
            now,
            tenantId,
            validName,
            p,
            normPhone,
            e,
            normEmail,
            CustomerStatus.Active,
            archivedAt: null);
    }

    public void UpdateDetails(
        string displayName,
        string? phone,
        string? email,
        DateTimeOffset now)
    {
        var validName = CustomerNormalization.ValidateDisplayName(displayName);
        var (p, normPhone) = CustomerNormalization.NormalizePhone(phone);
        var (e, normEmail) = CustomerNormalization.NormalizeEmail(email);

        ValidateContactRequirement(normPhone, normEmail);

        DisplayName = validName;
        Phone = p;
        NormalizedPhone = normPhone;
        Email = e;
        NormalizedEmail = normEmail;

        MarkUpdated(now);
    }

    public void Archive(DateTimeOffset now)
    {
        if (Status == CustomerStatus.Archived)
        {
            throw DomainException.Conflict("Customer is already archived.");
        }

        Status = CustomerStatus.Archived;
        ArchivedAt = now;
        MarkUpdated(now);
    }

    public void Restore(DateTimeOffset now)
    {
        if (Status == CustomerStatus.Active)
        {
            throw DomainException.Conflict("Customer is already active.");
        }

        Status = CustomerStatus.Active;
        ArchivedAt = null;
        MarkUpdated(now);
    }

    private static void ValidateContactRequirement(string? normalizedPhone, string? normalizedEmail)
    {
        if (string.IsNullOrWhiteSpace(normalizedPhone) && string.IsNullOrWhiteSpace(normalizedEmail))
        {
            throw DomainException.Validation("At least one contact method (phone or email) is required.");
        }
    }
}
