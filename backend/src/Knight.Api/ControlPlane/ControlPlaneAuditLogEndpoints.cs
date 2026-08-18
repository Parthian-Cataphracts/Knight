using AccessControl;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
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
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var result = await service.QueryAsync(
                new AuditLogQuery(page ?? 1, pageSize ?? 25, actorId, targetType, action, from, to),
                cancellationToken);

            var names = await labels.CustomerNamesAsync(
                result.Items.Where(entry => entry.CustomerId is not null)
                    .Select(entry => entry.CustomerId!.Value)
                    .Distinct()
                    .ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<AuditLogResponse>.Create(
                result.Items.Select(entry => ToResponse(entry, names)).ToArray(),
                result.Page,
                result.PageSize,
                result.TotalCount));
        }).RequirePermission(ControlPlanePermissions.AuditView);
    }

    private static AuditLogResponse ToResponse(AuditLogEntryView entry, IReadOnlyDictionary<Guid, string> customerNames) => new()
    {
        Id = entry.Id,
        CustomerName = entry.CustomerId is { } customerId ? customerNames.GetValueOrDefault(customerId) : null,

        // Automated work has no account behind it, so the actor type stands in
        // rather than leaving the column blank.
        Actor = string.IsNullOrWhiteSpace(entry.ActorDisplay) ? entry.ActorType : entry.ActorDisplay,
        Target = entry.TargetId is null ? entry.TargetType : $"{entry.TargetType} {entry.TargetId}",

        // The action name carries the outcome: a rejected attempt is recorded
        // under its own action rather than as a flag on the successful one.
        Result = entry.Action.EndsWith("failed", StringComparison.OrdinalIgnoreCase)
            || entry.Action.EndsWith("rejected", StringComparison.OrdinalIgnoreCase)
            || entry.Action.Contains("lockout", StringComparison.OrdinalIgnoreCase)
                ? "Failure"
                : "Success",
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
