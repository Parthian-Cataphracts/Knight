namespace Knight.Application.Exceptions;

/// <summary>
/// Raised when an authenticated caller is not permitted to perform the requested
/// action, including feature-not-enabled and missing-permission cases.
/// Translated to HTTP 403 at the API boundary.
/// </summary>
public class ForbiddenException : Exception
{
    public ForbiddenException(string message)
        : base(message)
    {
    }
}
