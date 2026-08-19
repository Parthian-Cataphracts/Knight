using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Customers.Domain;

/// <summary>
/// Something a person wrote down about a customer.
///
/// The audit trail records what the system did; this records what a human knows
/// and the system cannot infer — that a customer is migrating in April, that
/// their technical contact changed, that an outage was already discussed on the
/// phone. Support work is full of facts like these, and without somewhere to put
/// them they live in one person's memory.
///
/// Append-only, and attributed. A note that could be edited after the fact is a
/// note nobody can rely on later, which is the same reason the incident timeline
/// is append-only.
/// </summary>
public sealed class CustomerNote : Entity, ICustomerOwned
{
    public const int MaxBodyLength = 4000;

    public Guid CustomerId { get; private set; }

    public Guid AuthorId { get; private set; }

    /// <summary>
    /// The author's name as it was when they wrote it. Stored rather than joined
    /// because a note should still say who wrote it after that account is
    /// renamed or closed.
    /// </summary>
    public string AuthorName { get; private set; }

    public string Body { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private CustomerNote()
    {
        AuthorName = string.Empty;
        Body = string.Empty;
    }

    private CustomerNote(
        Guid id,
        Guid customerId,
        Guid authorId,
        string authorName,
        string body,
        DateTimeOffset createdAt)
        : base(id)
    {
        CustomerId = customerId;
        AuthorId = authorId;
        AuthorName = authorName;
        Body = body;
        CreatedAt = createdAt;
    }

    public static CustomerNote Write(
        Guid id,
        Guid customerId,
        Guid authorId,
        string? authorName,
        string? body,
        DateTimeOffset now)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("A note must belong to a customer.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw DomainException.Validation("A note must say something.");
        }

        var trimmed = body.Trim();

        return new CustomerNote(
            id,
            customerId,
            authorId,
            string.IsNullOrWhiteSpace(authorName) ? "Unknown" : authorName.Trim()[..Math.Min(authorName.Trim().Length, 200)],
            trimmed.Length <= MaxBodyLength ? trimmed : trimmed[..MaxBodyLength],
            now);
    }
}

public interface ICustomerNoteRepository
{
    Task<IReadOnlyCollection<CustomerNote>> ListAsync(Guid customerId, int limit, CancellationToken cancellationToken);

    Task AddAsync(CustomerNote note, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
