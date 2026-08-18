namespace Knight.Contracts.Common;

/// <summary>
/// Stable machine-readable error codes surfaced in Problem Details responses,
/// intended for client-side branching independent of the human-readable title.
/// </summary>
public static class ApiErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string Unauthorized = "unauthorized";
    public const string Conflict = "conflict";
    public const string Unexpected = "unexpected_error";
}
