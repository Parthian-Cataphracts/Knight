using AccessControl;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Read access to accounts, roles and the platform overview.
///
/// The overview reports only what the control plane actually knows today:
/// customers, stores, subscriptions, entitlements, invoices and the audit trail.
/// Server metrics, alerts and feature delivery arrive in phases 3.5 to 5, and
/// are absent here rather than reported as zero-shaped fiction.
/// </summary>
public static class ControlPlaneAccessEndpoints
{
    public static void MapControlPlaneAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapGroup("/api/v1/users")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Access");

        users.MapGet("/", async (
            int? page,
            int? pageSize,
            Guid? customerId,
            string? q,
            IAccessDirectory directory,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var result = await directory.ListUsersAsync(page ?? 1, pageSize ?? 25, customerId, q, cancellationToken);

            var roles = await labels.RoleNamesForUsersAsync(
                result.Items.Select(user => user.Id).ToArray(),
                cancellationToken);

            var names = await labels.CustomerNamesAsync(
                result.Items.Where(user => user.CustomerId is not null)
                    .Select(user => user.CustomerId!.Value)
                    .Distinct()
                    .ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<AccountResponse>.Create(
                result.Items.Select(user => ToResponse(user, roles, names)).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.UserView);

        users.MapPost("/", async (
            CreateAccountRequest request,
            IAccountAdministration administration,
            IControlPlanePrincipal principal,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            // A customer-scoped administrator creates accounts inside their own
            // customer, whatever the body asks for.
            var customerId = principal.CustomerId ?? request.CustomerId;

            var created = await administration.CreateAsync(
                request.Email, request.DisplayName, customerId, request.RoleIds, cancellationToken);

            var user = created.User;
            var roleNames = await labels.RoleNamesForUsersAsync([user.Id], cancellationToken);
            var customerNames = customerId is { } id
                ? await labels.CustomerNamesAsync([id], cancellationToken)
                : new Dictionary<Guid, string>();

            return Results.Created(
                $"/api/v1/users/{user.Id}",
                new CreatedAccountResponse
                {
                    Account = ToResponse(user, roleNames, customerNames),

                    // Present only where no invitation could be sent. The two
                    // are deliberately exclusive: a response carrying both would
                    // mean a password existed that nobody needed to know.
                    TemporaryPassword = created.TemporaryPassword,
                    InvitationSent = created.InvitationSent,
                });
        }).RequirePermission(ControlPlanePermissions.UserManage);

        users.MapPatch("/{id:guid}", async (
            Guid id,
            RenameAccountRequest request,
            IAccountAdministration administration,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var user = await administration.RenameAsync(id, request.DisplayName, cancellationToken);

            return Results.Ok(await DescribeAsync(user, labels, cancellationToken));
        }).RequirePermission(ControlPlanePermissions.UserManage);

        users.MapPost("/{id:guid}/activate", async (
            Guid id,
            IAccountAdministration administration,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var user = await administration.SetActiveAsync(id, true, cancellationToken);

            return Results.Ok(await DescribeAsync(user, labels, cancellationToken));
        }).RequirePermission(ControlPlanePermissions.UserManage);

        users.MapPost("/{id:guid}/suspend", async (
            Guid id,
            IAccountAdministration administration,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var user = await administration.SetActiveAsync(id, false, cancellationToken);

            return Results.Ok(await DescribeAsync(user, labels, cancellationToken));
        }).RequirePermission(ControlPlanePermissions.UserManage);

        users.MapPut("/{id:guid}/roles", async (
            Guid id,
            SetAccountRolesRequest request,
            IAccountAdministration administration,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var user = await administration.SetRolesAsync(id, request.RoleIds, cancellationToken);

            return Results.Ok(await DescribeAsync(user, labels, cancellationToken));
        }).RequirePermission(ControlPlanePermissions.UserManage);

        users.MapPost("/{id:guid}/mfa/reset", async (
            Guid id,
            IAccountAdministration administration,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var user = await administration.ResetMfaAsync(id, cancellationToken);

            return Results.Ok(await DescribeAsync(user, labels, cancellationToken));
        }).RequirePermission(ControlPlanePermissions.UserManage);

        users.MapPost("/{id:guid}/password/reset", async (
            Guid id,
            IAccountAdministration administration,
            CancellationToken cancellationToken) =>
        {
            // An account that has not activated yet gets a fresh invitation
            // rather than a password: it never had one, and handing one over
            // would undo the reason the invitation exists.
            if (await administration.ResendInvitationAsync(id, cancellationToken))
            {
                return Results.Ok(new TemporaryPasswordResponse { TemporaryPassword = null, InvitationSent = true });
            }

            var password = await administration.ResetPasswordAsync(id, cancellationToken);

            // Returned once. There is no endpoint that reads it back.
            return Results.Ok(new TemporaryPasswordResponse { TemporaryPassword = password, InvitationSent = false });
        }).RequirePermission(ControlPlanePermissions.UserManage);

        var roles = endpoints.MapGroup("/api/v1/roles")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Access");

        roles.MapGet("/", async (
            IAccessDirectory directory,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var items = await directory.ListRolesAsync(cancellationToken);
            var members = await labels.RoleMemberCountsAsync(cancellationToken);

            return Results.Ok(PagedResponse<RoleResponse>.Create(
                items.Select(role => ToResponse(role, members.GetValueOrDefault(role.Id))).ToArray(),
                1,
                items.Count,
                items.Count));
        }).RequirePermission(ControlPlanePermissions.RoleView);

        roles.MapPost("/", async (
            CreateRoleRequest request,
            IAccountAdministration administration,
            IControlPlanePrincipal principal,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<RoleScope>(request.Scope, ignoreCase: true, out var scope))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["scope"] = [$"'{request.Scope}' is not a recognised role scope."],
                });
            }

            var role = await administration.CreateRoleAsync(
                request.Name,
                request.Description ?? string.Empty,
                scope,
                principal.CustomerId ?? request.CustomerId,
                request.Permissions,
                cancellationToken);

            return Results.Created($"/api/v1/roles/{role.Id}", ToResponse(role, 0));
        }).RequirePermission(ControlPlanePermissions.RoleManage);

        roles.MapPut("/{id:guid}/permissions", async (
            Guid id,
            SetRolePermissionsRequest request,
            IAccountAdministration administration,
            CancellationToken cancellationToken) =>
        {
            var role = await administration.SetRolePermissionsAsync(id, request.Permissions, cancellationToken);

            return Results.Ok(ToResponse(role, 0));
        }).RequirePermission(ControlPlanePermissions.RoleManage);

        // The permission catalogue, so the role editor offers real keys rather
        // than a free-text box that silently accepts a typo.
        roles.MapGet("/permissions", () =>
                Results.Ok(PagedResponse<string>.Create(
                    [.. ControlPlanePermissions.AssignableToRoles],
                    1,
                    ControlPlanePermissions.AssignableToRoles.Count,
                    ControlPlanePermissions.AssignableToRoles.Count)))
            .RequirePermission(ControlPlanePermissions.RoleView);

        var monitoring = endpoints.MapGroup("/api/v1/monitoring")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Monitoring");

        monitoring.MapGet("/overview", async (
            IControlPlaneOverviewReader reader,
            CancellationToken cancellationToken) =>
        {
            var overview = await reader.ReadAsync(cancellationToken);

            return Results.Ok(new OverviewResponse
            {
                Customers = new CustomerCountsResponse
                {
                    Total = overview.Customers.Total,
                    Active = overview.Customers.Active,
                    Suspended = overview.Customers.Suspended,
                    Prospect = overview.Customers.Prospect,
                    Archived = overview.Customers.Archived,
                },
                Stores = new StoreCountsResponse
                {
                    Total = overview.Stores.Total,
                    Connected = overview.Stores.Connected,
                    Degraded = overview.Stores.Degraded,
                    Disconnected = overview.Stores.Disconnected,
                    NotRegistered = overview.Stores.NotRegistered,
                },
                Subscriptions = new SubscriptionCountsResponse
                {
                    Total = overview.Subscriptions.Total,
                    Active = overview.Subscriptions.Active,
                    Trial = overview.Subscriptions.Trial,
                    PastDue = overview.Subscriptions.PastDue,
                    Suspended = overview.Subscriptions.Suspended,
                    ActiveEntitlements = overview.Subscriptions.ActiveEntitlements,
                },
                Billing = new BillingCountsResponse
                {
                    Draft = overview.Billing.Draft,
                    Issued = overview.Billing.Issued,
                    Overdue = overview.Billing.Overdue,
                    Paid = overview.Billing.Paid,
                    OutstandingTotal = overview.Billing.OutstandingTotal,
                    Currency = overview.Billing.Currency,
                },
                RecentActivity = overview.RecentActivity
                    .Select(entry => new ActivityResponse
                    {
                        Id = entry.Id,
                        Action = entry.Action,
                        TargetType = entry.TargetType,
                        TargetId = entry.TargetId,
                        Actor = entry.Actor,
                        OccurredAt = entry.OccurredAt,
                    })
                    .ToArray(),
            });
        }).RequirePermission(ControlPlanePermissions.MonitoringView);
    }

    private static AccountResponse ToResponse(
        ControlPlaneUser user,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> roles,
        IReadOnlyDictionary<Guid, string> customerNames) => new()
        {
            Id = user.Id,
            Scope = user.IsPlatformStaff ? "Platform" : "Customer",
            Roles = roles.GetValueOrDefault(user.Id) ?? [],
            CustomerName = user.CustomerId is { } customerId ? customerNames.GetValueOrDefault(customerId) : null,
            Email = user.Email,
            DisplayName = user.DisplayName,
            CustomerId = user.CustomerId,
            Status = user.Status.ToString(),
            MfaEnabled = user.MfaEnabled,
            LastLoginAt = user.LastLoginAt,
            CreatedAt = user.CreatedAt,
        };

    private static RoleResponse ToResponse(Role role, int userCount) => new()
    {
        Id = role.Id,
        PermissionCount = role.Permissions.Count,
        UserCount = userCount,
        Name = role.Name,
        Description = role.Description,
        Scope = role.Scope.ToString(),
        IsSystem = role.IsSystem,
        CustomerId = role.CustomerId,
        Permissions = role.Permissions.Select(permission => permission.PermissionKey).OrderBy(key => key).ToArray(),
    };

    /// <summary>
    /// Describes an account with its role and customer names, resolved together.
    /// Shared by every write endpoint so they all answer in one shape.
    /// </summary>
    private static async Task<AccountResponse> DescribeAsync(
        ControlPlaneUser user,
        ILabelReader labels,
        CancellationToken cancellationToken)
    {
        var roleNames = await labels.RoleNamesForUsersAsync([user.Id], cancellationToken);

        var customerNames = user.CustomerId is { } id
            ? await labels.CustomerNamesAsync([id], cancellationToken)
            : new Dictionary<Guid, string>();

        return ToResponse(user, roleNames, customerNames);
    }
}
