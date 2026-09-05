using System.Globalization;
using System.Text;
using AccessControl.Domain;
using Ingestion;
using Ingestion.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Contracts.Common;
using Knight.Contracts.ControlPlane;

namespace Knight.Api.ControlPlane;

/// <summary>
/// The structured log stream stores ship, read centrally across every store
/// (docs/api-contracts.md §2, docs/risks.md §3.4).
///
/// Reading it needs <c>logs.view</c>, which is a permission of its own: a log
/// line is the least redacted thing in the control plane, and being allowed to
/// see that a store exists is a long way from being allowed to read what it
/// logged.
///
/// The stream can be narrowed by store, by an exact level or by a minimum
/// severity — which is how the errors, warnings and alerts are separated from the
/// noise — by a time range, and by a full-text search of the message, and the
/// same filter can be exported as CSV.
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
            string? minSeverity,
            string? search,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? page,
            int? pageSize,
            IIngestionService ingestion,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var filter = new LogFilter(storeId, level, minSeverity, search, from, to);

            var (items, total) = await ingestion.ListLogsAsync(
                filter,
                page ?? 1,
                pageSize ?? 50,
                cancellationToken);

            var names = await labels.StoreNamesAsync(
                items.Select(entry => entry.StoreId).Distinct().ToArray(),
                cancellationToken);

            return Results.Ok(PagedResponse<StoreLogEntryResponse>.Create(
                items.Select(entry => ToResponse(entry, names)).ToArray(),
                page ?? 1,
                pageSize ?? 50,
                total));
        }).RequirePermission(ControlPlanePermissions.LogsView);

        // The same filter as the stream, delivered as a CSV file rather than a
        // page. Bounded by the service so a broad export cannot pull a store's
        // whole history at once; when it clips, the last row is still the newest.
        group.MapGet("/export", async (
            Guid? storeId,
            string? level,
            string? minSeverity,
            string? search,
            DateTimeOffset? from,
            DateTimeOffset? to,
            IIngestionService ingestion,
            ILabelReader labels,
            CancellationToken cancellationToken) =>
        {
            var filter = new LogFilter(storeId, level, minSeverity, search, from, to);

            // int.MaxValue asks for "as many as allowed"; the service clamps it to
            // its own export cap so one endpoint does not own that number.
            var items = await ingestion.ExportLogsAsync(filter, int.MaxValue, cancellationToken);

            var names = await labels.StoreNamesAsync(
                items.Select(entry => entry.StoreId).Distinct().ToArray(),
                cancellationToken);

            var csv = BuildCsv(items, names);
            var name = $"logs-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv";
            return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", name);
        }).RequirePermission(ControlPlanePermissions.LogsExport);
    }

    private static StoreLogEntryResponse ToResponse(StoreLogEntry entry, IReadOnlyDictionary<Guid, string> names) => new()
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
    };

    private static string BuildCsv(IReadOnlyCollection<StoreLogEntry> items, IReadOnlyDictionary<Guid, string> names)
    {
        var builder = new StringBuilder();
        builder.Append("Timestamp,Level,Service,Store,Environment,TraceId,Message").Append('\n');

        foreach (var entry in items)
        {
            builder
                .Append(Field(entry.Timestamp.ToString("o", CultureInfo.InvariantCulture))).Append(',')
                .Append(Field(NormaliseLevel(entry.Level))).Append(',')
                .Append(Field(entry.Service ?? "store")).Append(',')
                .Append(Field(names.GetValueOrDefault(entry.StoreId) ?? entry.StoreId.ToString())).Append(',')
                .Append(Field(entry.Environment)).Append(',')
                .Append(Field(entry.TraceId ?? string.Empty)).Append(',')
                .Append(Field(entry.Message))
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders a CSV field. Every field is quoted and embedded quotes doubled per
    /// RFC 4180, and a value that opens with a formula character is prefixed with
    /// an apostrophe first — quoting alone does not stop a spreadsheet running a
    /// message that begins with '=' or '+', and a log line is attacker-influenced
    /// text.
    /// </summary>
    private static string Field(string value)
    {
        var guarded = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return $"\"{guarded.Replace("\"", "\"\"")}\"";
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
