using AccessControl.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;
using Servers;
using Servers.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Servers, agents, metrics, alerts and the monitoring overview
/// (docs/domain-model.md §7).
///
/// Every route here is platform-only. There is no customer-scoped view of
/// infrastructure by design: a customer sees the health of their store, never the
/// machine it runs on or what else runs beside it (docs/authorization.md). That
/// is enforced by the permissions, all of which are platform permissions no
/// customer role holds.
/// </summary>
public static class ControlPlaneServerEndpoints
{
    public static void MapControlPlaneServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapServers(endpoints);
        MapMonitoring(endpoints);
    }

    private static void MapServers(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/servers")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Servers");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            string? environment,
            string? status,
            bool? includeDecommissioned,
            IServerService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<ServerEnvironment>(environment, out var parsedEnvironment))
            {
                return ValidationProblem("environment", $"'{environment}' is not a recognised environment.");
            }

            if (!TryParse<ServerStatus>(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised server status.");
            }

            var result = await service.ListAsync(
                page ?? 1, pageSize ?? 25, parsedEnvironment, parsedStatus,
                includeDecommissioned ?? false, cancellationToken);

            return Results.Ok(PagedResponse<ServerResponse>.Create(
                [.. result.Items.Select(ServerMapping.ToResponse)],
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.ServerView);

        group.MapGet("/{id:guid}", async (Guid id, IServerService service, CancellationToken cancellationToken) =>
        {
            var server = await service.GetAsync(id, cancellationToken);
            return server is null ? Results.NotFound() : Results.Ok(server.ToResponse());
        }).RequirePermission(ControlPlanePermissions.ServerView);

        group.MapPost("/", async (
            RegisterServerRequest request,
            IServerService service,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<ServerHostingModel>(request.HostingModel, ignoreCase: true, out var hostingModel))
            {
                return ValidationProblem("hostingModel", $"'{request.HostingModel}' is not a recognised hosting model.");
            }

            if (!Enum.TryParse<ServerEnvironment>(request.Environment, ignoreCase: true, out var environment))
            {
                return ValidationProblem("environment", $"'{request.Environment}' is not a recognised environment.");
            }

            var server = await service.RegisterAsync(
                new RegisterServerInput(request.Name, hostingModel, environment, request.Provider, request.Region, request.IpAddress),
                cancellationToken);

            return Results.Created($"/api/v1/servers/{server.Id}", server.ToResponse());
        }).RequirePermission(ControlPlanePermissions.ServerManage);

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateServerRequest request,
            IServerService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.UpdateAsync(
                id,
                new UpdateServerInput(request.Name, request.Provider, request.Region, request.IpAddress),
                cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.ServerManage);

        group.MapPost("/{id:guid}/decommission", async (
            Guid id,
            IServerService service,
            CancellationToken cancellationToken) =>
        {
            await service.DecommissionAsync(id, cancellationToken);
            return Results.NoContent();
        }).RequirePermission(ControlPlanePermissions.ServerManage);

        group.MapGet("/{id:guid}/metrics", async (
            Guid id,
            int? limit,
            IServerService service,
            CancellationToken cancellationToken) =>
        {
            var metrics = await service.ListMetricsAsync(id, limit ?? 100, cancellationToken);
            return Results.Ok(metrics.Select(ServerMapping.ToResponse).ToList());
        }).RequirePermission(ControlPlanePermissions.ServerView);

        group.MapGet("/{id:guid}/agents", async (
            Guid id,
            IServerService service,
            CancellationToken cancellationToken) =>
        {
            var agents = await service.ListAgentsAsync(id, cancellationToken);
            return Results.Ok(agents.Select(ServerMapping.ToResponse).ToList());
        }).RequirePermission(ControlPlanePermissions.ServerView);

        // Issuing a provisioning token is the one action here that hands out a
        // secret, so it takes the stronger permission and is shown exactly once.
        group.MapPost("/{id:guid}/agents", async (
            Guid id,
            IServerService service,
            CancellationToken cancellationToken) =>
        {
            var issued = await service.ProvisionAgentAsync(id, cancellationToken);

            return Results.Created(
                $"/api/v1/servers/{id}/agents/{issued.AgentId}",
                new ProvisioningTokenResponse(issued.AgentId, issued.ServerId, issued.Token, issued.ExpiresAt));
        }).RequirePermission(ControlPlanePermissions.AgentManage);

        group.MapPost("/agents/{agentId:guid}/revoke", async (
            Guid agentId,
            RevokeAgentRequest request,
            IServerService service,
            CancellationToken cancellationToken) =>
        {
            await service.RevokeAgentAsync(agentId, request.Reason, cancellationToken);
            return Results.NoContent();
        }).RequirePermission(ControlPlanePermissions.AgentManage);
    }

    private static void MapMonitoring(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/monitoring")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Monitoring");

        group.MapGet("/overview", async (IMonitoringService service, CancellationToken cancellationToken) =>
            Results.Ok(ServerMapping.ToResponse(await service.GetOverviewAsync(cancellationToken))))
            .RequirePermission(ControlPlanePermissions.ServerView);

        group.MapGet("/alerts", async (
            int? page,
            int? pageSize,
            string? severity,
            string? source,
            bool? openOnly,
            IMonitoringService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<AlertSeverity>(severity, out var parsedSeverity))
            {
                return ValidationProblem("severity", $"'{severity}' is not a recognised severity.");
            }

            if (!TryParse<AlertSource>(source, out var parsedSource))
            {
                return ValidationProblem("source", $"'{source}' is not a recognised alert source.");
            }

            var result = await service.ListAlertsAsync(
                page ?? 1, pageSize ?? 25, parsedSeverity, parsedSource, openOnly ?? true, cancellationToken);

            return Results.Ok(PagedResponse<AlertResponse>.Create(
                [.. result.Items.Select(ServerMapping.ToResponse)],
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.ServerView);

        group.MapPost("/alerts/{id:guid}/acknowledge", async (
            Guid id,
            IMonitoringService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.AcknowledgeAlertAsync(id, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.ServerManage);

        group.MapPost("/alerts/{id:guid}/resolve", async (
            Guid id,
            IMonitoringService service,
            CancellationToken cancellationToken) =>
            Results.Ok((await service.ResolveAlertAsync(id, cancellationToken)).ToResponse()))
            .RequirePermission(ControlPlanePermissions.ServerManage);
    }

    private static bool TryParse<TEnum>(string? value, out TEnum? parsed) where TEnum : struct, Enum
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var result))
        {
            return false;
        }

        parsed = result;
        return true;
    }

    private static IResult ValidationProblem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
