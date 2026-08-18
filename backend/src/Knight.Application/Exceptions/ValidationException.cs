namespace Knight.Application.Exceptions;

/// <summary>
/// Raised when request input fails application-level validation.
/// Translated to HTTP 400 (Problem Details) at the API boundary.
/// </summary>
public class ValidationException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }
}
