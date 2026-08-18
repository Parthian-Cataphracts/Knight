using Knight.Api.Authorization;
using Identity;
using Knight.Application.Authorization;
using Knight.Contracts.AccessControl;

namespace Knight.Api.Endpoints;

/// <summary>Exposes the registered permission catalog for future admin UIs.</summary>
public static class TenantPermissionEndpoints
{
    public static void MapTenantPermissionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tenant/permissions", (IPermissionCatalog catalog) =>
            {
                var response = catalog.All
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new PermissionResponse { Key = p.Key, Description = p.Description, Module = p.Module })
                    .ToArray();

                return Results.Ok(response);
            })
            .RequireAuthorization("TenantUserOnly")
            .RequireAuthorization(p => p.RequirePermission(IdentityPermissions.RolesView))
            .RequireRateLimiting("platform")
            .WithTags("Tenant Permissions");
    }
}
