using System.Security.Claims;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.ControlPlane.Security;

namespace Knight.Api.Ingest;

/// <summary>
/// Reads the store claims off the current request.
///
/// A store principal is not a small user: it has no roles, no permissions and no
/// access to anything but the ingestion surface. What it may do is decided by
/// which endpoints exist for it, never by anything it presents
/// (docs/authentication.md §4).
/// </summary>
public interface IStorePrincipal
{
    bool IsStore { get; }

    Guid? StoreId { get; }

    Guid? CustomerId { get; }

    /// <summary>The environment the store is registered as, from the token — never from the payload.</summary>
    string? Environment { get; }

    string? ClientId { get; }
}

internal sealed class HttpStorePrincipal : IStorePrincipal
{
    private readonly ClaimsPrincipal? _user;

    public HttpStorePrincipal(IHttpContextAccessor accessor)
    {
        _user = accessor.HttpContext?.User;
    }

    public bool IsStore => _user?.FindFirstValue(PrincipalTypes.ClaimType) == PrincipalTypes.Store;

    public Guid? StoreId => IsStore ? ParseGuid(_user?.FindFirstValue(StoreClaims.StoreId)) : null;

    public Guid? CustomerId => IsStore ? ParseGuid(_user?.FindFirstValue(ControlPlaneClaims.CustomerId)) : null;

    public string? Environment => IsStore ? _user?.FindFirstValue(StoreClaims.StoreEnvironment) : null;

    public string? ClientId => IsStore ? _user?.FindFirstValue(StoreClaims.ClientId) : null;

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : null;
}
