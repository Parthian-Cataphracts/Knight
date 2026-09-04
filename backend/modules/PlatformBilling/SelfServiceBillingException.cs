using Knight.Application.Exceptions;

namespace PlatformBilling;

/// <summary>
/// A self-service billing failure with one of the documented stable error codes
/// (docs/self-service-saas-plan.md §6). Carried through the ordinary
/// exception-handling middleware by <see cref="ICodedException"/>, so the caller
/// sees <c>PLAN_UNAVAILABLE</c> rather than a generic <c>conflict</c>.
/// </summary>
public sealed class SelfServiceBillingException : Exception, ICodedException
{
    private SelfServiceBillingException(int statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }

    public string ErrorCode { get; }

    public static SelfServiceBillingException PlanUnavailable(string message) =>
        new(StatusCodes.Status409Conflict, "PLAN_UNAVAILABLE", message);

    public static SelfServiceBillingException InvalidFeatureSelection(string message) =>
        new(StatusCodes.Status400BadRequest, "INVALID_FEATURE_SELECTION", message);

    private static class StatusCodes
    {
        public const int Status400BadRequest = 400;
        public const int Status409Conflict = 409;
    }
}
