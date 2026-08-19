using Knight.Application.Abstractions.ControlPlane;
using Servers.Domain;

namespace Servers;

/// <summary>
/// Lets a module that is not this one raise an alert.
///
/// The port speaks in strings rather than in this module's enums, deliberately.
/// Observability needs to say "a feature has drifted on this store" without
/// taking a reference on the module that happens to own the alert table, and
/// alerting needs to stay the only place deduplication is implemented. Trading
/// a little type safety at this one boundary buys both.
///
/// An unrecognised severity or source is coerced rather than rejected. A caller
/// that misspells "Critical" has a bug worth fixing, but losing the alert
/// entirely — during whatever incident prompted it — is the worse outcome of the
/// two.
/// </summary>
internal sealed class AlertRaiser : IAlertRaiser
{
    private readonly IMonitoringService _monitoring;
    private readonly IAlertRepository _alerts;

    public AlertRaiser(IMonitoringService monitoring, IAlertRepository alerts)
    {
        _monitoring = monitoring;
        _alerts = alerts;
    }

    public async Task<(Guid AlertId, bool IsNew)> RaiseAsync(
        string ruleKey,
        string severity,
        string source,
        Guid sourceId,
        Guid? customerId,
        string message,
        CancellationToken cancellationToken)
    {
        // Asked before raising, because RaiseAsync deliberately does not
        // distinguish the two cases in its return — and every caller of this
        // port needs to, since "still broken" and "just broke" deserve different
        // responses.
        var existing = await _alerts.FindOpenAsync(ruleKey, sourceId, cancellationToken);

        var alert = await _monitoring.RaiseAsync(
            ParseSource(source),
            sourceId,
            ParseSeverity(severity),
            ruleKey,
            message,
            customerId,
            cancellationToken);

        return (alert.Id, existing is null);
    }

    public async Task<bool> ResolveAsync(string ruleKey, Guid sourceId, CancellationToken cancellationToken)
    {
        var open = await _alerts.FindOpenAsync(ruleKey, sourceId, cancellationToken);

        if (open is null)
        {
            return false;
        }

        await _monitoring.ResolveAlertAsync(open.Id, cancellationToken);

        return true;
    }

    private static AlertSeverity ParseSeverity(string severity) =>
        Enum.TryParse<AlertSeverity>(severity, ignoreCase: true, out var parsed)
            ? parsed
            : AlertSeverity.Warning;

    private static AlertSource ParseSource(string source) =>
        Enum.TryParse<AlertSource>(source, ignoreCase: true, out var parsed)
            ? parsed
            : AlertSource.Store;
}
