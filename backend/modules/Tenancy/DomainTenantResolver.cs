using Knight.Application.Abstractions.Tenancy;
using Tenancy.Domain;

namespace Tenancy;

/// <summary>
/// Resolves the current tenant from an authenticated tenant-user claim and/or the
/// request host, and validates that the resolved tenant is actually active. Platform
/// admin requests are never routed through this resolver — see
/// <c>TenantResolutionMiddleware</c>, which recognizes the platform-admin principal
/// type before tenant resolution is attempted at all.
/// </summary>
public sealed class DomainTenantResolver : ITenantResolver
{
    public const string TenantIdClaimType = "tenant_id";

    private readonly ITenantRepository _tenantRepository;

    public DomainTenantResolver(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    public async Task<TenantResolutionResult> ResolveAsync(TenantResolutionRequest request, CancellationToken cancellationToken)
    {
        Guid? claimTenantId = null;
        var claimValue = request.Principal?.FindFirst(TenantIdClaimType)?.Value;
        if (claimValue is not null)
        {
            if (!Guid.TryParse(claimValue, out var parsed))
            {
                return TenantResolutionResult.NotResolved("The tenant claim on the authenticated token is malformed.");
            }

            claimTenantId = parsed;
        }

        Tenant? hostTenant = null;
        if (!string.IsNullOrWhiteSpace(request.Host))
        {
            string normalizedHost;
            try
            {
                normalizedHost = DomainHostFormat.Normalize(request.Host);
            }
            catch (FormatException)
            {
                normalizedHost = string.Empty;
            }

            if (normalizedHost.Length > 0)
            {
                hostTenant = await _tenantRepository.GetByDomainHostAsync(normalizedHost, cancellationToken);
            }
        }

        if (claimTenantId.HasValue && hostTenant is not null && hostTenant.Id != claimTenantId.Value)
        {
            return TenantResolutionResult.Conflict(
                "The authenticated tenant token does not match the tenant that owns the request host.");
        }

        var targetTenantId = claimTenantId ?? hostTenant?.Id;
        if (!targetTenantId.HasValue)
        {
            return TenantResolutionResult.NotResolved("No tenant could be resolved from the request host or claims.");
        }

        // hostTenant already carries the entity when resolution came from the host;
        // otherwise (claim-only resolution) it must be loaded to check status.
        var tenant = hostTenant ?? await _tenantRepository.GetByIdAsync(targetTenantId.Value, cancellationToken);
        if (tenant is null)
        {
            return TenantResolutionResult.NotResolved("The resolved tenant no longer exists.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return TenantResolutionResult.Blocked(tenant.Id, $"Tenant is {tenant.Status.ToString().ToLowerInvariant()}.");
        }

        return TenantResolutionResult.Resolved(tenant.Id);
    }
}
