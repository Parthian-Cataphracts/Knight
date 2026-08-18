namespace Knight.Application.Exceptions;

/// <summary>
/// Raised when a requested resource does not exist or is not visible to the
/// current tenant/platform context. Translated to HTTP 404 at the API boundary.
/// </summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message)
        : base(message)
    {
    }

    public NotFoundException(string entityName, object key)
        : base($"{entityName} with identifier '{key}' was not found.")
    {
    }
}
