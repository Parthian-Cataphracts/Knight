namespace Knight.Application.Exceptions;

/// <summary>
/// An exception that names its own HTTP status and a stable, machine-readable
/// error code, so a caller can branch on the failure rather than parse a
/// sentence. The self-service billing and provisioning surfaces use this to
/// return the documented codes — <c>PLAN_UNAVAILABLE</c>,
/// <c>INVALID_FEATURE_SELECTION</c>, <c>PAYMENT_REQUIRED</c> and the rest
/// (docs/self-service-saas-plan.md §6) — through the one exception-handling
/// middleware, without that middleware knowing which module raised them.
/// </summary>
public interface ICodedException
{
    int StatusCode { get; }

    string ErrorCode { get; }
}
