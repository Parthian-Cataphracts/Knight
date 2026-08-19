using AccessControl.Domain;
using Ingestion;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// The structured log stream stores ship (docs/api-contracts.md §2).
///
/// Reading it needs <c>logs.view</c>, which is a permission of its own: a log
/// line is the least redacted thing in the control plane, and being allowed to
/// see that a store exists is a long way from being allowed to read what it
/// logged.
///
/// Filtering, full-text search and export land with observability in phase 7;
/// what exists here is the stream, newest first, so the capability a customer
/// pays for is visible rather than merely stored.
/// </summary>
public static class ControlPlaneLogEndpoints
{
    public static void MapControlPlaneLogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/logs")
            .RequireAuthorization(ControlPlaneAuthorizationExtensions.UserPolicy)
            .RequireRateLimiting("control-plane")
            .WithTags("Logs");

        group.MapGet("/", async (
            Guid? storeId,
            string? level,
            int? page,
            int? pageSize,
            IIngestionService ingestion,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var (items, total) = await ingestion.ListLogsAsync(
                storeId,
                level,
                page ?? 1,
                pageSize ?? 50,
                cancellationToken);

            var names = await labels.StoreNamesAsync(
                items.Select(entry => entry.StoreId).Distinct().ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<StoreLogEntryResponse>.Create(
                items.Select(entry => new StoreLogEntryResponse
                {
                    Id = entry.Id,
                    Timestamp = entry.Timestamp,
                    Level = NormaliseLevel(entry.Level),
                    Service = entry.Service ?? "store",
                    StoreId = entry.StoreId,
                    StoreName = names.GetValueOrDefault(entry.StoreId),
                    Environment = entry.Environment,
                    Message = entry.Message,
                    TraceId = entry.TraceId,
                }).ToArray(),
                page ?? 1,
                pageSize ?? 50,
                total));
        }).RequirePermission(ControlPlanePermissions.LogsView);
    }

    /// <summary>
    /// Stores log in whatever vocabulary their framework uses. The stream is
    /// stored as sent and normalised on the way out, so a filter means the same
    /// thing across stores without KNIGHT having rewritten what anyone logged.
    /// </summary>
    private static string NormaliseLevel(string level) => level.Trim().ToUpperInvariant() switch
    {
        "TRACE" or "DEBUG" => "Debug",
        "INFO" or "INFORMATION" or "NOTICE" => "Information",
        "WARN" or "WARNING" => "Warning",
        "ERROR" or "ERR" => "Error",
        "CRITICAL" or "FATAL" or "ALERT" or "EMERGENCY" => "Critical",
        _ => "Information",
    };
}
