using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;
using Observability;
using Observability.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Grouped errors, incidents and notification channels (docs/api-contracts.md §2).
///
/// Reading and acting are split into separate permissions throughout —
/// <c>errors.view</c> against <c>errors.manage</c>, <c>incident.view</c> against
/// <c>incident.manage</c>. Marking a problem resolved is a claim other people
/// will act on, and being allowed to see that something is broken is a long way
/// from being allowed to declare it fixed.
///
/// Everything is customer-scoped by the persistence filter, so no route here
/// takes a customer id: a customer principal sees their errors, a platform
/// principal sees all of them, and neither can be changed by getting a query
/// wrong in this file.
/// </summary>
public static class ControlPlaneObservabilityEndpoints
{
    public static void MapControlPlaneObservabilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapErrors(endpoints);
        MapIncidents(endpoints);
        MapNotifications(endpoints);
    }

    private static void MapErrors(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/errors")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Errors");

        group.MapGet("/groups", async (
            Guid? storeId,
            string? status,
            string? environment,
            int? page,
            int? pageSize,
            IErrorService errors,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<ErrorGroupStatus>(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised error status.");
            }

            var (items, total) = await errors.ListGroupsAsync(
                storeId, parsedStatus, environment, page ?? 1, pageSize ?? 25, cancellationToken);

            var names = await labels.StoreNamesAsync(
                items.Select(item => item.StoreId).Distinct().ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<ErrorGroupResponse>.Create(
                [.. items.Select(item => ToResponse(item, names.GetValueOrDefault(item.StoreId)))],
                page ?? 1,
                pageSize ?? 25,
                total));
        }).RequirePermission(ControlPlanePermissions.ErrorsView);

        group.MapGet("/groups/{id:guid}", async (
            Guid id,
            IErrorService errors,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var item = await errors.GetGroupAsync(id, cancellationToken);
            var names = await labels.StoreNamesAsync([item.StoreId], cancellationToken);

            return Results.Ok(ToResponse(item, names.GetValueOrDefault(item.StoreId)));
        }).RequirePermission(ControlPlanePermissions.ErrorsView);

        group.MapGet("/groups/{id:guid}/events", async (
            Guid id,
            int? limit,
            IErrorService errors,
            CancellationToken cancellationToken) =>
        {
            var samples = await errors.ListSamplesAsync(id, limit ?? 20, cancellationToken);

            // The same envelope every other collection endpoint returns. A bare
            // array here would be a second, undocumented shape for the client to
            // handle, and the one screen that reads it would silently render
            // empty rather than fail.
            var mapped = samples.Select(sample => new ErrorEventSampleResponse
            {
                Id = sample.Id,
                OccurredAt = sample.OccurredAt,
                Version = sample.StoreVersion,
                RequestId = sample.RequestId,
                TraceId = sample.TraceId,
                StackTrace = sample.StackTrace,
                Message = sample.Message,
                Endpoint = sample.Endpoint,
                StatusCode = sample.StatusCode,
            }).ToArray();

            return Results.Ok(PagedResponse<ErrorEventSampleResponse>.Create(
                mapped, 1, mapped.Length, mapped.Length));
        }).RequirePermission(ControlPlanePermissions.ErrorsView);

        group.MapPost("/groups/{id:guid}/acknowledge", async (
            Guid id,
            IErrorService errors,
            IControlPlanePrincipal principal,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var item = await errors.AcknowledgeAsync(id, principal.UserId ?? Guid.Empty, cancellationToken);
            var names = await labels.StoreNamesAsync([item.StoreId], cancellationToken);

            return Results.Ok(ToResponse(item, names.GetValueOrDefault(item.StoreId)));
        }).RequirePermission(ControlPlanePermissions.ErrorsManage);

        group.MapPost("/groups/{id:guid}/resolve", async (
            Guid id,
            ResolveErrorGroupRequest? request,
            IErrorService errors,
            IControlPlanePrincipal principal,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var item = await errors.ResolveAsync(
                id, principal.UserId ?? Guid.Empty, request?.InVersion, cancellationToken);

            var names = await labels.StoreNamesAsync([item.StoreId], cancellationToken);

            return Results.Ok(ToResponse(item, names.GetValueOrDefault(item.StoreId)));
        }).RequirePermission(ControlPlanePermissions.ErrorsManage);

        group.MapPost("/groups/{id:guid}/ignore", async (
            Guid id,
            IErrorService errors,
            IControlPlanePrincipal principal,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var item = await errors.IgnoreAsync(id, principal.UserId ?? Guid.Empty, cancellationToken);
            var names = await labels.StoreNamesAsync([item.StoreId], cancellationToken);

            return Results.Ok(ToResponse(item, names.GetValueOrDefault(item.StoreId)));
        }).RequirePermission(ControlPlanePermissions.ErrorsManage);

        group.MapPost("/groups/{id:guid}/reopen", async (
            Guid id,
            IErrorService errors,
            IControlPlanePrincipal principal,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var item = await errors.ReopenAsync(id, principal.UserId ?? Guid.Empty, cancellationToken);
            var names = await labels.StoreNamesAsync([item.StoreId], cancellationToken);

            return Results.Ok(ToResponse(item, names.GetValueOrDefault(item.StoreId)));
        }).RequirePermission(ControlPlanePermissions.ErrorsManage);
    }

    private static void MapIncidents(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/incidents")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Incidents");

        group.MapGet("/", async (
            string? status,
            string? severity,
            Guid? storeId,
            bool? openOnly,
            int? page,
            int? pageSize,
            IIncidentService incidents,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<IncidentStatus>(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised incident status.");
            }

            if (!TryParse<IncidentSeverity>(severity, out var parsedSeverity))
            {
                return ValidationProblem("severity", $"'{severity}' is not a recognised severity.");
            }

            var (items, total) = await incidents.ListAsync(
                parsedStatus, parsedSeverity, storeId, openOnly ?? false, page ?? 1, pageSize ?? 25, cancellationToken);

            var names = await labels.StoreNamesAsync(
                items.Where(item => item.StoreId is not null).Select(item => item.StoreId!.Value).Distinct().ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<IncidentResponse>.Create(
                [.. items.Select(item => ToResponse(item, names))],
                page ?? 1,
                pageSize ?? 25,
                total));
        }).RequirePermission(ControlPlanePermissions.IncidentView);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IIncidentService incidents,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var incident = await incidents.GetAsync(id, cancellationToken);
            var names = incident.StoreId is { } storeId
                ? await labels.StoreNamesAsync([storeId], cancellationToken)
                : new Dictionary<Guid, string>();

            return Results.Ok(ToResponse(incident, names));
        }).RequirePermission(ControlPlanePermissions.IncidentView);

        group.MapGet("/{id:guid}/events", async (
            Guid id,
            IIncidentService incidents,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var timeline = await incidents.ListTimelineAsync(id, cancellationToken);

            // Actor names are resolved in one lookup for the whole timeline
            // rather than per entry: an incident that ran for a day has a lot of
            // entries and very few distinct people.
            var actors = await labels.UserNamesAsync(
                [.. timeline.Where(entry => entry.ActorId is not null).Select(entry => entry.ActorId!.Value).Distinct()],
                cancellationToken);

            var mapped = timeline.Select(entry => new IncidentEventResponse
            {
                Id = entry.Id,
                OccurredAt = entry.OccurredAt,
                Type = entry.Type.ToString(),
                Actor = entry.ActorId is null ? "System" : actors.GetValueOrDefault(entry.ActorId.Value, "—"),
                Message = entry.Message,
            }).ToArray();

            return Results.Ok(PagedResponse<IncidentEventResponse>.Create(
                mapped, 1, mapped.Length, mapped.Length));
        }).RequirePermission(ControlPlanePermissions.IncidentView);

        group.MapPost("/", async (
            OpenIncidentRequest request,
            IIncidentService incidents,
            IControlPlanePrincipal principal,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<IncidentSeverity>(request.Severity, ignoreCase: true, out var severity))
            {
                return ValidationProblem("severity", $"'{request.Severity}' is not a recognised severity.");
            }

            var incident = await incidents.OpenAsync(
                request.Title,
                severity,
                principal.UserId ?? Guid.Empty,
                // A customer principal can only ever open an incident against
                // their own customer, whatever the body says.
                principal.CustomerId ?? request.CustomerId,
                request.StoreId,
                request.ServerId,
                request.Summary,
                cancellationToken);

            var names = incident.StoreId is { } storeId
                ? await labels.StoreNamesAsync([storeId], cancellationToken)
                : new Dictionary<Guid, string>();

            return Results.Created($"/api/v1/incidents/{incident.Id}", ToResponse(incident, names));
        }).RequirePermission(ControlPlanePermissions.IncidentManage);

        group.MapPost("/{id:guid}/acknowledge", async (
            Guid id,
            IncidentNoteRequest? request,
            IIncidentService incidents,
            IControlPlanePrincipal principal,
            CancellationToken cancellationToken) =>
        {
            var incident = await incidents.AcknowledgeAsync(
                id, principal.UserId ?? Guid.Empty, request?.Message, cancellationToken);

            return Results.Ok(ToResponse(incident, new Dictionary<Guid, string>()));
        }).RequirePermission(ControlPlanePermissions.IncidentManage);

        group.MapPost("/{id:guid}/mitigate", async (
            Guid id,
            IncidentNoteRequest request,
            IIncidentService incidents,
            IControlPlanePrincipal principal,
            CancellationToken cancellationToken) =>
        {
            var incident = await incidents.MitigateAsync(
                id, principal.UserId ?? Guid.Empty, request.Message, cancellationToken);

            return Results.Ok(ToResponse(incident, new Dictionary<Guid, string>()));
        }).RequirePermission(ControlPlanePermissions.IncidentManage);

        group.MapPost("/{id:guid}/resolve", async (
            Guid id,
            ResolveIncidentRequest? request,
            IIncidentService incidents,
            IControlPlanePrincipal principal,
            CancellationToken cancellationToken) =>
        {
            var incident = await incidents.ResolveAsync(
                id, principal.UserId ?? Guid.Empty, request?.RootCause, cancellationToken);

            return Results.Ok(ToResponse(incident, new Dictionary<Guid, string>()));
        }).RequirePermission(ControlPlanePermissions.IncidentManage);

        group.MapPost("/{id:guid}/reopen", async (
            Guid id,
            ReopenIncidentRequest request,
            IIncidentService incidents,
            IControlPlanePrincipal principal,
            CancellationToken cancellationToken) =>
        {
            var incident = await incidents.ReopenAsync(
                id, principal.UserId ?? Guid.Empty, request.Reason, cancellationToken);

            return Results.Ok(ToResponse(incident, new Dictionary<Guid, string>()));
        }).RequirePermission(ControlPlanePermissions.IncidentManage);

        group.MapPost("/{id:guid}/notes", async (
            Guid id,
            IncidentNoteRequest request,
            IIncidentService incidents,
            IControlPlanePrincipal principal,
            CancellationToken cancellationToken) =>
        {
            var incident = await incidents.AddNoteAsync(
                id, principal.UserId ?? Guid.Empty, request.Message, cancellationToken);

            return Results.Ok(ToResponse(incident, new Dictionary<Guid, string>()));
        }).RequirePermission(ControlPlanePermissions.IncidentManage);
    }

    private static void MapNotifications(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/notifications")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Notifications");

        group.MapGet("/channels", async (
            bool? includeDisabled,
            INotificationService notifications,
            IControlPlanePrincipal principal,
            CancellationToken cancellationToken) =>
        {
            var channels = await notifications.ListChannelsAsync(
                principal.CustomerId, includeDisabled ?? true, cancellationToken);

            var mapped = channels.Select(ToResponse).ToArray();

            return Results.Ok(PagedResponse<NotificationChannelResponse>.Create(
                mapped, 1, mapped.Length, mapped.Length));
        }).RequirePermission(ControlPlanePermissions.NotificationManage);

        group.MapPost("/channels", async (
            CreateNotificationChannelRequest request,
            INotificationService notifications,
            IControlPlanePrincipal principal,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<NotificationChannelKind>(request.Kind, ignoreCase: true, out var kind))
            {
                return ValidationProblem("kind", $"'{request.Kind}' is not a recognised channel kind.");
            }

            if (!Enum.TryParse<NotificationSeverity>(request.MinimumSeverity, ignoreCase: true, out var severity))
            {
                return ValidationProblem("minimumSeverity", $"'{request.MinimumSeverity}' is not a recognised severity.");
            }

            var channel = await notifications.CreateChannelAsync(
                // A customer principal creates channels for their own customer,
                // full stop. Only platform staff — who have no customer id — can
                // create a platform channel or name a customer.
                principal.CustomerId ?? request.CustomerId,
                request.Name,
                kind,
                request.Endpoint,
                severity,
                request.RuleFilter,
                request.Secret,
                cancellationToken);

            return Results.Created($"/api/v1/notifications/channels/{channel.Id}", ToResponse(channel));
        }).RequirePermission(ControlPlanePermissions.NotificationManage);

        group.MapPut("/channels/{id:guid}", async (
            Guid id,
            UpdateNotificationChannelRequest request,
            INotificationService notifications,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<NotificationSeverity>(request.MinimumSeverity, ignoreCase: true, out var severity))
            {
                return ValidationProblem("minimumSeverity", $"'{request.MinimumSeverity}' is not a recognised severity.");
            }

            var channel = await notifications.UpdateChannelAsync(
                id, request.Name, severity, request.RuleFilter, cancellationToken);

            return Results.Ok(ToResponse(channel));
        }).RequirePermission(ControlPlanePermissions.NotificationManage);

        group.MapPost("/channels/{id:guid}/enable", async (
            Guid id,
            INotificationService notifications,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await notifications.SetChannelEnabledAsync(id, true, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.NotificationManage);

        group.MapPost("/channels/{id:guid}/disable", async (
            Guid id,
            INotificationService notifications,
            CancellationToken cancellationToken) =>
            Results.Ok(ToResponse(await notifications.SetChannelEnabledAsync(id, false, cancellationToken))))
            .RequirePermission(ControlPlanePermissions.NotificationManage);

        group.MapPost("/channels/{id:guid}/test", async (
            Guid id,
            INotificationService notifications,
            CancellationToken cancellationToken) =>
        {
            var result = await notifications.TestChannelAsync(id, cancellationToken);

            return Results.Ok(new NotificationTestResponse
            {
                Succeeded = result.Succeeded,
                Error = result.Error,
            });
        }).RequirePermission(ControlPlanePermissions.NotificationManage);

        group.MapGet("/", async (
            Guid? channelId,
            string? status,
            int? page,
            int? pageSize,
            INotificationService notifications,
            CancellationToken cancellationToken) =>
        {
            if (!TryParse<NotificationDeliveryStatus>(status, out var parsedStatus))
            {
                return ValidationProblem("status", $"'{status}' is not a recognised delivery status.");
            }

            var (items, total) = await notifications.ListDeliveriesAsync(
                channelId, parsedStatus, page ?? 1, pageSize ?? 25, cancellationToken);

            return Results.Ok(PagedResponse<NotificationDeliveryResponse>.Create(
                [.. items.Select(ToResponse)],
                page ?? 1,
                pageSize ?? 25,
                total));
        }).RequirePermission(ControlPlanePermissions.MonitoringView);

        group.MapPost("/{id:guid}/read", async (
            Guid id,
            INotificationService notifications,
            CancellationToken cancellationToken) =>
        {
            await notifications.MarkReadAsync(id, cancellationToken);

            return Results.NoContent();
        }).RequirePermission(ControlPlanePermissions.MonitoringView);

        // The rule catalogue, so the settings screen can offer real keys rather
        // than a free-text box that silently accepts a typo.
        group.MapGet("/rules", () =>
                Results.Ok(PagedResponse<string>.Create(
                    [.. ObservabilityRules.All], 1, ObservabilityRules.All.Count, ObservabilityRules.All.Count)))
            .RequirePermission(ControlPlanePermissions.NotificationManage);
    }

    private static ErrorGroupResponse ToResponse(ErrorGroup group, string? storeName) => new()
    {
        Id = group.Id,
        StoreId = group.StoreId,
        StoreName = storeName,
        Environment = group.Environment,
        ExceptionType = group.ExceptionType,
        Title = group.Title,
        Endpoint = group.Endpoint,
        OccurrenceCount = group.OccurrenceCount,
        Status = group.Status.ToString(),
        FirstSeenAt = group.FirstSeenAt,
        LastSeenAt = group.LastSeenAt,
        FirstSeenVersion = group.FirstSeenVersion,
        LastSeenVersion = group.LastSeenVersion,
        IsRegression = group.IsRegression,
        IncidentId = group.IncidentId,
    };

    private static IncidentResponse ToResponse(Incident incident, IReadOnlyDictionary<Guid, string> storeNames) => new()
    {
        Id = incident.Id,
        Reference = incident.Reference,
        Title = incident.Title,
        Summary = incident.Summary,
        Severity = incident.Severity.ToString(),
        Status = incident.Status.ToString(),
        CustomerId = incident.CustomerId,
        StoreId = incident.StoreId,
        StoreName = incident.StoreId is { } id ? storeNames.GetValueOrDefault(id) : null,
        ServerId = incident.ServerId,
        ServerName = null,
        RuleKey = incident.RuleKey,
        OpenedAt = incident.OpenedAt,
        AcknowledgedAt = incident.AcknowledgedAt,
        MitigatedAt = incident.MitigatedAt,
        ResolvedAt = incident.ResolvedAt,
        RootCause = incident.RootCause,
    };

    private static NotificationChannelResponse ToResponse(NotificationChannel channel) => new()
    {
        Id = channel.Id,
        CustomerId = channel.CustomerId,
        Name = channel.Name,
        Kind = channel.Kind.ToString(),
        Endpoint = channel.Endpoint,
        MinimumSeverity = channel.MinimumSeverity.ToString(),
        RuleFilter = channel.RuleFilter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
        IsEnabled = channel.IsEnabled,
        DisabledReason = channel.DisabledReason,
        LastDeliveredAt = channel.LastDeliveredAt,
        ConsecutiveFailures = channel.ConsecutiveFailures,

        // Whether a secret exists, never the secret. Not to the customer who set
        // it, not to platform staff, not once.
        HasSecret = channel.SecretCipher is not null,
    };

    private static NotificationDeliveryResponse ToResponse(NotificationDelivery delivery) => new()
    {
        Id = delivery.Id,
        ChannelId = delivery.ChannelId,
        ChannelName = null,
        Severity = delivery.Severity.ToString(),
        RuleKey = delivery.RuleKey,
        Subject = delivery.Subject.ToString(),
        SubjectId = delivery.SubjectId,
        Title = delivery.Title,
        Body = delivery.Body,
        Status = delivery.Status.ToString(),
        AttemptCount = delivery.AttemptCount,
        CreatedAt = delivery.CreatedAt,
        DeliveredAt = delivery.DeliveredAt,
        ReadAt = delivery.ReadAt,
        LastError = delivery.LastError,
    };

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
