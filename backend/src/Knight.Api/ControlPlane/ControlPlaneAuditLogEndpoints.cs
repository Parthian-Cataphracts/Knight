using AccessControl;
using AccessControl.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Read access to the audit trail. Entries are filtered by the same customer
/// isolation as everything else, so a customer sees their own history and
/// nothing about the platform or other customers.
/// </summary>
public static class ControlPlaneAuditLogEndpoints
{
    public static void MapControlPlaneAuditLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/audit-logs")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Audit");

        group.MapGet("/", async (
            int? page,
            int? pageSize,
            Guid? actorId,
            string? targetType,
            string? action,
            DateTimeOffset? from,
            DateTimeOffset? to,
            IAuditLogQueryService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.QueryAsync(
                new AuditLogQuery(page ?? 1, pageSize ?? 25, actorId, targetType, action, from, to),
                cancellationToken);

            return Results.Ok(PagedResponse<AuditLogResponse>.Create(
                result.Items.Select(ToResponse).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.AuditView);
    }

    private static AuditLogResponse ToResponse(AuditLogEntryView entry) => new()
    {
        Id = entry.Id,
        ActorType = entry.ActorType,
        ActorUserId = entry.ActorUserId,
        ActorDisplay = entry.ActorDisplay,
        CustomerId = entry.CustomerId,
        Action = entry.Action,
        TargetType = entry.TargetType,
        TargetId = entry.TargetId,
        PreviousValue = entry.PreviousValue,
        NewValue = entry.NewValue,
        CorrelationId = entry.CorrelationId,
        IpAddress = entry.IpAddress,
        OccurredAt = entry.OccurredAt,
    };
}
