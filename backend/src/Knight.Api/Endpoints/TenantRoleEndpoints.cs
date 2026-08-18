using Knight.Api.Authorization;
using Identity;
using Knight.Application.Abstractions.Tenancy;
using Knight.Contracts.AccessControl;
using Knight.Contracts.Common;

namespace Knight.Api.Endpoints;

public static class TenantRoleEndpoints
{
    public static void MapTenantRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/roles")
            .RequireAuthorization("TenantUserOnly")
            .RequireRateLimiting("platform")
            .WithTags("Tenant Roles");

        group.MapGet("/", async (int? page, int? pageSize, ITenantContext tenantContext, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
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
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.RolesView));

        group.MapGet("/{id:guid}", async (Guid id, ITenantContext tenantContext, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var role = await service.GetAsync(tenantId, id, cancellationToken);
            if (role is null)
            {
                return Results.NotFound();
            }

            var permissionKeys = await service.GetPermissionKeysAsync(tenantId, id, cancellationToken);
            return Results.Ok(new RoleDetailResponse { Id = role.Id, Name = role.Name, PermissionKeys = permissionKeys, CreatedAt = role.CreatedAt });
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.RolesView));

        group.MapPost("/", async (CreateRoleRequest request, ITenantContext tenantContext, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var role = await service.CreateAsync(tenantId, new CreateRoleInput(request.Name, request.PermissionKeys), cancellationToken);
            return Results.Created($"/api/tenant/roles/{role.Id}", new RoleDetailResponse
            {
                Id = role.Id,
                Name = role.Name,
                PermissionKeys = request.PermissionKeys,
                CreatedAt = role.CreatedAt
            });
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.RolesCreate));

        group.MapPut("/{id:guid}", async (Guid id, UpdateRoleRequest request, ITenantContext tenantContext, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var role = await service.RenameAsync(tenantId, id, request.Name, cancellationToken);
            var permissionKeys = await service.GetPermissionKeysAsync(tenantId, id, cancellationToken);
            return Results.Ok(new RoleDetailResponse { Id = role.Id, Name = role.Name, PermissionKeys = permissionKeys, CreatedAt = role.CreatedAt });
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.RolesUpdate));

        group.MapPut("/{id:guid}/permissions", async (Guid id, SetRolePermissionsRequest request, ITenantContext tenantContext, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            var permissionKeys = await service.SetPermissionsAsync(tenantId, id, request.PermissionKeys, cancellationToken);
            var role = await service.GetAsync(tenantId, id, cancellationToken);
            return Results.Ok(new RoleDetailResponse { Id = role!.Id, Name = role.Name, PermissionKeys = permissionKeys, CreatedAt = role.CreatedAt });
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.RolesAssignPermissions));

        group.MapDelete("/{id:guid}", async (Guid id, ITenantContext tenantContext, IRoleManagementService service, CancellationToken cancellationToken) =>
        {
            var tenantId = TenantStaffEndpoints.RequireTenant(tenantContext);
            await service.DeleteAsync(tenantId, id, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequirePermission(IdentityPermissions.RolesDelete));
    }
}
