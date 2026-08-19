using AccessControl.Domain;
using Customers.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// The summary panels the dashboard shows beside its main tables: platform
/// service health, the report catalogue, the plan/feature matrix, a customer's
/// activity and notes, and a store's measured usage.
///
/// Each of these reads across module boundaries, so none of them belongs to a
/// module's own endpoint file. What they have in common is that every figure is
/// something KNIGHT actually measures — where a number would have to be
/// invented, the field is absent and the screen says so rather than showing a
/// plausible fiction.
/// </summary>
public static class ControlPlaneInsightEndpoints
{
    public static void MapControlPlaneInsightEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Insights");

        group.MapGet("/infrastructure/services", async (
            IInsightReader insights,
            CancellationToken cancellationToken) =>
        {
            var services = await insights.ReadServicesAsync(cancellationToken);

            return Results.Ok(new { items = services.Select(ToResponse).ToArray() });
        }).RequirePermission(ControlPlanePermissions.MonitoringView);

        group.MapGet("/reports", async (
            IInsightReader insights,
            CancellationToken cancellationToken) =>
        {
            var reports = await insights.ReadReportsAsync(cancellationToken);

            return Results.Ok(new
            {
                items = reports.Select(report => new ReportSummaryResponse
                {
                    Key = report.Key,
                    Name = report.Name,
                    Description = report.Description,
                    UpdatedAt = report.UpdatedAt,
                }).ToArray(),
            });
        }).RequirePermission(ControlPlanePermissions.ReportView);

        group.MapGet("/plans/entitlement-matrix", async (
            IInsightReader insights,
            CancellationToken cancellationToken) =>
        {
            var rows = await insights.ReadEntitlementMatrixAsync(cancellationToken);

            return Results.Ok(new
            {
                items = rows.Select(row => new EntitlementMatrixRowResponse
                {
                    FeatureSlug = row.FeatureSlug,
                    FeatureName = row.FeatureName,
                    Values = row.Values,
                }).ToArray(),
            });
        }).RequirePermission(ControlPlanePermissions.PlanView);

        group.MapGet("/customers/{customerId:guid}/activity", async (
            Guid customerId,
            int? limit,
            IInsightReader insights,
            CancellationToken cancellationToken) =>
        {
            var items = await insights.ReadCustomerActivityAsync(
                customerId, Math.Clamp(limit ?? 50, 1, 200), cancellationToken);

            return Results.Ok(new
            {
                items = items.Select(item => new ActivityItemResponse
                {
                    Id = item.Id,
                    OccurredAt = item.OccurredAt,
                    Kind = item.Kind,
                    Title = item.Title,
                    Actor = item.Actor,
                }).ToArray(),
            });
        }).RequirePermission(ControlPlanePermissions.CustomerView);

        // Reading a customer's notes needs no more than the right to see the
        // customer; writing one is a separate permission, because a note is a
        // durable statement other people will act on.
        group.MapGet("/customers/{customerId:guid}/notes", async (
            Guid customerId,
            int? limit,
            ICustomerNoteRepository notes,
            CancellationToken cancellationToken) =>
        {
            var items = await notes.ListAsync(customerId, Math.Clamp(limit ?? 50, 1, 200), cancellationToken);

            return Results.Ok(new
            {
                items = items.Select(ToResponse).ToArray(),
            });
        }).RequirePermission(ControlPlanePermissions.CustomerView);

        group.MapPost("/customers/{customerId:guid}/notes", async (
            Guid customerId,
            CreateCustomerNoteRequest request,
            ICustomerNoteRepository notes,
            IControlPlanePrincipal principal,
            IAuditTrail audit,
            IDateTimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var note = CustomerNote.Write(
                Guid.NewGuid(),
                customerId,
                principal.UserId ?? Guid.Empty,
                principal.Email,
                request.Body,
                clock.UtcNow);

            await notes.AddAsync(note, cancellationToken);
            await notes.SaveChangesAsync(cancellationToken);

            // Audited without its body: that a note was written is operational
            // history, and duplicating free text a person typed into a second
            // table serves nobody.
            await audit.RecordAsync(
                "customer.note.added",
                "CustomerNote",
                note.Id.ToString(),
                customerId,
                cancellationToken);

            return Results.Created($"/api/v1/customers/{customerId}/notes", ToResponse(note));
        }).RequirePermission(ControlPlanePermissions.CustomerUpdate);

        group.MapGet("/stores/{storeId:guid}/usage", async (
            Guid storeId,
            int? hours,
            IInsightReader insights,
            CancellationToken cancellationToken) =>
        {
            var usage = await insights.ReadStoreUsageAsync(storeId, hours ?? 24, cancellationToken);

            if (usage is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                items = new[]
                {
                    new StoreUsageResponse
                    {
                        Errors = usage.Errors,
                        Logs = usage.Logs,
                        HealthLatencyMs = usage.HealthLatencyMs,
                        WindowHours = usage.WindowHours,
                        TotalErrors = usage.TotalErrors,
                        TotalLogs = usage.TotalLogs,
                    },
                },
            });
        }).RequirePermission(ControlPlanePermissions.StoreView);
    }

    private static PlatformServiceResponse ToResponse(PlatformServiceStatus service) => new()
    {
        Key = service.Key,
        Name = service.Name,
        Detail = service.Detail,
        Status = service.Status,
        Metrics = service.Metrics.Select(metric => new[] { metric.Key, metric.Value }).ToArray(),
    };

    private static CustomerNoteResponse ToResponse(CustomerNote note) => new()
    {
        Id = note.Id,
        Author = note.AuthorName,
        CreatedAt = note.CreatedAt,
        Body = note.Body,
    };
}
