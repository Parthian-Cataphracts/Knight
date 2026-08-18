namespace Knight.Domain.Exceptions;

/// <summary>
/// Semantic category of a domain rule violation. This is a domain-level signal,
/// not a transport concern: the domain never knows or names an HTTP status code.
/// The API layer alone decides how each category is surfaced over HTTP.
/// </summary>
public enum DomainErrorCategory
{
    /// <summary>
    /// The request collides with existing state: a value already taken, a record
    /// still referenced by others, or an operation illegal for the current status
    /// of the aggregate. The same input could succeed against different state.
    /// </summary>
    Conflict = 0,

    /// <summary>
    /// The input itself is malformed, out of range, or self-contradictory, and is
    /// invalid regardless of any data already stored.
    /// </summary>
    Validation = 1
}
