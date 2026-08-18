using Identity;
using Identity.Domain;
using Knight.Contracts.AccessControl;
using Knight.Contracts.Common;

namespace Knight.Api.Endpoints;

/// <summary>
/// Platform-authorized tenant staff management — the future Super Admin's
/// control-plane access, and the bootstrap path for a tenant's first staff
/// account before any tenant administrator exists. The target tenant comes
/// from the route (Platform context intentionally manages tenants); the
/// underlying application service is the same one Tenant self-administration
/// uses, with PlatformAdmin bypassing the delegation-subset check.
/// See docs/architecture/authorization.md.
/// </summary>
public static class PlatformStaffEndpoints
{
    public static void MapPlatformStaffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/staff")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Tenant Staff");

        group.MapGet("/", async (Guid tenantId, int? page, int? pageSize, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, cancellationToken);
            return Results.Ok(PagedResponse<StaffResponse>.Create(result.Items.Select(TenantStaffEndpoints.ToResponse).ToArray(), result.Page, result.PageSize, result.TotalCount));
        });

        group.MapPost("/", async (Guid tenantId, CreateStaffRequest request, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var user = await service.CreateAsync(tenantId, new CreateStaffInput(request.Email, request.DisplayName, request.InitialPassword, request.RoleIds), cancellationToken);
            return Results.Created($"/api/platform/tenants/{tenantId}/staff/{user.Id}", TenantStaffEndpoints.ToResponse(new StaffListItem { User = user, RoleIds = request.RoleIds }));
        });

        group.MapPost("/{userId:guid}/enable", async (Guid tenantId, Guid userId, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var user = await service.EnableAsync(tenantId, userId, cancellationToken);
            return Results.Ok(TenantStaffEndpoints.ToResponse(new StaffListItem { User = user, RoleIds = [] }));
        });

        group.MapPost("/{userId:guid}/disable", async (Guid tenantId, Guid userId, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var user = await service.DisableAsync(tenantId, userId, cancellationToken);
            return Results.Ok(TenantStaffEndpoints.ToResponse(new StaffListItem { User = user, RoleIds = [] }));
        });

        group.MapPut("/{userId:guid}/roles", async (Guid tenantId, Guid userId, ReplaceStaffRolesRequest request, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            var roleIds = await service.ReplaceRolesAsync(tenantId, userId, request.RoleIds, cancellationToken);
            var user = await service.GetAsync(tenantId, userId, cancellationToken);
            return Results.Ok(TenantStaffEndpoints.ToResponse(new StaffListItem { User = user!, RoleIds = roleIds }));
        });

        group.MapPost("/{userId:guid}/sessions/revoke", async (Guid tenantId, Guid userId, IStaffManagementService service, CancellationToken cancellationToken) =>
        {
            await service.RevokeSessionsAsync(tenantId, userId, cancellationToken);
            return Results.NoContent();
        });
    }
}
