using Knight.Api.Middleware;
using Knight.Application.Abstractions.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Supplies the acting principal to the audit trail from the current request,
/// so application services never read HTTP themselves. A request with no
/// authenticated user is attributed to the system — background seeding and
/// startup work, not an anonymous person.
/// </summary>
internal sealed class HttpAuditContext : IAuditContext
{
    private readonly IControlPlanePrincipal _principal;
    private readonly IHttpContextAccessor _accessor;

    public HttpAuditContext(IControlPlanePrincipal principal, IHttpContextAccessor accessor)
    {
        _principal = principal;
        _accessor = accessor;
    }

    public AuditActorType ActorType => _principal.IsControlPlaneUser ? AuditActorType.User : AuditActorType.System;

    public Guid? ActorUserId => _principal.IsControlPlaneUser ? _principal.UserId : null;

    public string? ActorDisplay => _principal.Email;

    public string? CorrelationId =>
        _accessor.HttpContext?.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();

    public string? IpAddress => _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
