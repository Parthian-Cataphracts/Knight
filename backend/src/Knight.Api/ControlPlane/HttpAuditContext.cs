using Knight.Api.BackgroundServices;
using Knight.Api.Ingest;
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
    private readonly IStorePrincipal _store;
    private readonly IHttpContextAccessor _accessor;

    public HttpAuditContext(IControlPlanePrincipal principal, IStorePrincipal store, IHttpContextAccessor accessor)
    {
        _principal = principal;
        _store = store;
        _accessor = accessor;
    }

    public AuditActorType ActorType => this switch
    {
        _ when _principal.IsControlPlaneUser => AuditActorType.User,
        _ when _store.IsStore => AuditActorType.Store,
        _ => AuditActorType.System,
    };

    public Guid? ActorUserId => _principal.IsControlPlaneUser ? _principal.UserId : null;

    /// <summary>
    /// A store is identified by its client id, which is not a secret and is what
    /// an operator reads in the store's own configuration. The secret behind it
    /// never reaches an audit entry.
    /// </summary>
    public string? ActorDisplay => _principal.Email ?? (_store.IsStore ? _store.ClientId : null);

    /// <summary>
    /// The request's correlation id, or — for the background workers, which have
    /// no request — the id of the pass the work belongs to.
    /// </summary>
    public string? CorrelationId
    {
        get
        {
            var fromRequest = _accessor.HttpContext?.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();

            return string.IsNullOrEmpty(fromRequest)
                ? BackgroundCorrelation.CorrelationId
                : fromRequest;
        }
    }

    public string? IpAddress => _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
