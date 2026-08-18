namespace Knight.Domain.Exceptions;

/// <summary>
/// Raised when a domain invariant is violated. Application and presentation layers
/// translate this into an appropriate API response; it must never be swallowed silently.
///
/// Every instance carries a <see cref="DomainErrorCategory"/> so the API boundary can
/// distinguish malformed input from a genuine state conflict without inspecting
/// message strings. The category is a semantic signal only — the domain stays free of
/// transport concerns and never references HTTP.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message)
        : this(message, DomainErrorCategory.Conflict)
    {
    }

    public DomainException(string message, Exception innerException)
        : this(message, DomainErrorCategory.Conflict, innerException)
    {
    }

    public DomainException(string message, DomainErrorCategory category)
        : base(message)
    {
        Category = category;
    }

    public DomainException(string message, DomainErrorCategory category, Exception innerException)
        : base(message, innerException)
    {
        Category = category;
    }

    /// <summary>
    /// Whether this violation describes malformed input or a collision with existing state.
    /// </summary>
    public DomainErrorCategory Category { get; }

    /// <summary>
    /// Creates a violation for input that is invalid regardless of stored data
    /// (out-of-range numbers, contradictory selection rules, malformed identifiers).
    /// </summary>
    public static DomainException Validation(string message) =>
        new(message, DomainErrorCategory.Validation);

    /// <summary>
    /// Creates a violation for well-formed input that collides with existing state
    /// (a value already taken, an operation illegal for the aggregate's current status).
    /// </summary>
    public static DomainException Conflict(string message) =>
        new(message, DomainErrorCategory.Conflict);
}
