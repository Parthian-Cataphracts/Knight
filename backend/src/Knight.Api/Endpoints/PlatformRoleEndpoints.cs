using Identity;
using Knight.Contracts.AccessControl;
using Knight.Contracts.Common;

namespace Knight.Api.Endpoints;

/// <summary>
/// Platform-authorized tenant role management. See
/// docs/architecture/authorization.md and <see cref="PlatformStaffEndpoints"/>.
/// </summary>
public static class PlatformRoleEndpoints
{
    public static void MapPlatformRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/roles")
            .RequireAuthorization("PlatformAdminOnly")
            .RequireRateLimiting("platform")
            .WithTags("Platform Tenant Roles");

        group.MapGet("/", async (Guid tenantId, int? page, int? pageSize, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var result = await service.ListAsync(tenantId, page ?? 1, pageSize ?? 20, cancellationToken);
            var items = result.Items.Select(i => new RoleResponse
            {
                Id = i.Role.Id,
                Name = i.Role.Name,
                PermissionCount = i.PermissionCount,
                AssignedUserCount = i.AssignedUserCount,
                CreatedAt = i.Role.CreatedAt
            }).ToArray();

            return Results.Ok(PagedResponse<RoleResponse>.Create(items, result.Page, result.PageSize, result.TotalCount));
        });

        group.MapPost("/", async (Guid tenantId, CreateRoleRequest request, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var role = await service.CreateAsync(tenantId, new CreateRoleInput(request.Name, request.PermissionKeys), cancellationToken);
            return Results.Created($"/api/platform/tenants/{tenantId}/roles/{role.Id}", new RoleDetailResponse
            {
                Id = role.Id,
                Name = role.Name,
                PermissionKeys = request.PermissionKeys,
                CreatedAt = role.CreatedAt
            });
        });

        group.MapPut("/{roleId:guid}", async (Guid tenantId, Guid roleId, UpdateRoleRequest request, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var role = await service.RenameAsync(tenantId, roleId, request.Name, cancellationToken);
            var permissionKeys = await service.GetPermissionKeysAsync(tenantId, roleId, cancellationToken);
            return Results.Ok(new RoleDetailResponse { Id = role.Id, Name = role.Name, PermissionKeys = permissionKeys, CreatedAt = role.CreatedAt });
        });

        group.MapPut("/{roleId:guid}/permissions", async (Guid tenantId, Guid roleId, SetRolePermissionsRequest request, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var permissionKeys = await service.SetPermissionsAsync(tenantId, roleId, request.PermissionKeys, cancellationToken);
            var role = await service.GetAsync(tenantId, roleId, cancellationToken);
            return Results.Ok(new RoleDetailResponse { Id = role!.Id, Name = role.Name, PermissionKeys = permissionKeys, CreatedAt = role.CreatedAt });
        });

        group.MapDelete("/{roleId:guid}", async (Guid tenantId, Guid roleId, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(tenantId, roleId, cancellationToken);
            return Results.NoContent();
        });
    }
}
