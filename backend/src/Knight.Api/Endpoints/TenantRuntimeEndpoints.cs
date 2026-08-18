using Knight.Application.Abstractions.Features;
using Knight.Application.Abstractions.Tenancy;
using Knight.Application.Exceptions;
using Knight.Contracts.Platform;
using Tenancy;

namespace Knight.Api.Endpoints;

/// <summary>
/// Minimal tenant-runtime surface used to validate the tenant-resolution and
/// feature-enforcement architecture end to end. Deliberately not a business
/// endpoint — returns only safe, already-public-to-the-tenant metadata.
/// </summary>
public static class TenantRuntimeEndpoints
{
    public static void MapTenantRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tenant/me", async (
                ITenantContext tenantContext,
                ITenantManagementService tenantManagementService,
                IFeatureAccessService featureAccessService,
                CancellationToken cancellationToken) =>
            {
                if (!tenantContext.HasTenant)
                {
                    throw new ForbiddenException("This endpoint requires an authenticated tenant context.");
                }

                var tenant = await tenantManagementService.GetAsync(tenantContext.TenantId!.Value, cancellationToken)
                    ?? throw new NotFoundException("Tenant", tenantContext.TenantId!.Value);

                var enabledFeatures = await featureAccessService.GetEnabledFeatureKeysAsync(tenant.Id, cancellationToken);

                return Results.Ok(new CurrentTenantResponse
                {
                    Id = tenant.Id,
                    Name = tenant.Name,
                    Slug = tenant.Slug,
                    EnabledFeatures = enabledFeatures
                });
            })
            .RequireAuthorization("TenantUserOnly")
            .RequireRateLimiting("tenant-public")
            .WithTags("Tenant Runtime");
    }
}
