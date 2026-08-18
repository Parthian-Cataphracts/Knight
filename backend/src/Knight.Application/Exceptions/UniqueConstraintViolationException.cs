namespace Knight.Application.Exceptions;

/// <summary>
/// Raised by persistence implementations when a database unique-constraint
/// violation is detected on save — the last line of defense against race
/// conditions that in-memory/application-level uniqueness checks cannot fully
/// prevent. Callers translate this into a more specific <see cref="ConflictException"/>
/// where they can name the conflicting value.
/// </summary>
public class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
