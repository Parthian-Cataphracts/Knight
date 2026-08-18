using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Customers.Domain;

/// <summary>
/// A paying customer of the control plane: the business whose stores KNIGHT
/// manages. Not to be confused with a store's own end consumers, which live in
/// the store's Django application and are never modelled here
/// (docs/migration-plan.md, "Terminology collision").
///
/// The lifecycle is enforced by the aggregate rather than by callers, so an
/// illegal transition is impossible regardless of which service is calling.
/// </summary>
public sealed class Customer : AuditableEntity
{
    public string Name { get; private set; }

    public string? LegalName { get; private set; }

    public string ContactEmail { get; private set; }

    public string? Phone { get; private set; }

    public CustomerStatus Status { get; private set; }

    public string? Notes { get; private set; }

    private Customer()
    {
        Name = string.Empty;
        ContactEmail = string.Empty;
    }

    private Customer(Guid id, DateTimeOffset createdAt, string name, string contactEmail)
        : base(id, createdAt)
    {
        Name = name;
        ContactEmail = contactEmail;
        Status = CustomerStatus.Prospect;
    }

    public static Customer Create(Guid id, DateTimeOffset createdAt, string name, string contactEmail)
        => new(id, createdAt, ValidateName(name), CustomerNormalization.NormalizeEmail(contactEmail));

    public void UpdateProfile(
        string name,
        string? legalName,
        string contactEmail,
        string? phone,
        DateTimeOffset now)
    {
        EnsureNotArchived();

        Name = ValidateName(name);
        LegalName = string.IsNullOrWhiteSpace(legalName) ? null : legalName.Trim();
        ContactEmail = CustomerNormalization.NormalizeEmail(contactEmail);
        Phone = CustomerNormalization.NormalizePhone(phone);
        MarkUpdated(now);
    }

    public void SetNotes(string? notes, DateTimeOffset now)
    {
        EnsureNotArchived();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        MarkUpdated(now);
    }

    // --- Lifecycle -------------------------------------------------------
    //
    // Prospect --Activate--> Active --Suspend--> Suspended
    //                          |                     |
    //                       Archive              Activate / Archive
    //                          v                     v
    //                       Archived (terminal)

    public void Activate(DateTimeOffset now)
    {
        if (Status is not (CustomerStatus.Prospect or CustomerStatus.Suspended))
        {
            throw DomainException.Conflict($"A customer in status '{Status}' cannot be activated.");
        }

        Status = CustomerStatus.Active;
        MarkUpdated(now);
    }

    public void Suspend(DateTimeOffset now)
    {
        if (Status is not CustomerStatus.Active)
        {
            throw DomainException.Conflict($"A customer in status '{Status}' cannot be suspended.");
        }

        Status = CustomerStatus.Suspended;
        MarkUpdated(now);
    }

    /// <summary>
    /// Terminal. An archived customer keeps its rows for audit and billing
    /// history but can never be reactivated by an ordinary operation.
    /// </summary>
    public void Archive(DateTimeOffset now)
    {
        if (Status is not (CustomerStatus.Active or CustomerStatus.Suspended or CustomerStatus.Prospect))
        {
            throw DomainException.Conflict($"A customer in status '{Status}' cannot be archived.");
        }

        Status = CustomerStatus.Archived;
        MarkUpdated(now);
    }

    /// <summary>
    /// True when the customer may hold entitlements and operate stores. Feature
    /// delivery must refuse to act for a customer that is not operable.
    /// </summary>
    public bool IsOperable => Status is CustomerStatus.Active;

    private void EnsureNotArchived()
    {
        if (Status is CustomerStatus.Archived)
        {
            throw DomainException.Conflict("An archived customer cannot be modified.");
        }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw DomainException.Validation("Customer name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 200)
        {
            throw DomainException.Validation("Customer name must be 200 characters or fewer.");
        }

        return trimmed;
    }
}
