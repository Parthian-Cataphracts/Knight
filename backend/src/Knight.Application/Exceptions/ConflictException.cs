namespace Knight.Application.Exceptions;

/// <summary>
/// Raised when a request conflicts with existing state (e.g. a unique constraint
/// such as a duplicate slug or domain). Translated to HTTP 409 at the API boundary.
/// Distinct from <see cref="Knight.Domain.Exceptions.DomainException"/>, which
/// signals an invariant violation rather than a uniqueness conflict.
/// </summary>
public class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }
}
