using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Observability;
using Knight.Infrastructure.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Observability.Domain;
using Stores.Domain;

namespace Knight.Infrastructure.Telemetry;

/// <summary>
/// Reads the gauge values a collector asks for.
///
/// Every query here is a count against an index and nothing else. This runs on
/// the scrape path, which means it runs on a timer forever, and it runs when the
/// system is already unwell — a gauge that is expensive to read becomes load
/// arriving at the worst possible moment, and a monitoring system that adds to
/// an outage is worse than none.
///
/// It reads in platform scope deliberately: these are figures about KNIGHT as a
/// whole, not about one customer, and the customer that happens to be in scope
/// when a collector scrapes is nobody in particular.
/// </summary>
internal sealed class ObservabilityGaugeSource : IObservabilityGaugeSource
{
    private readonly IServiceScopeFactory _scopes;

    public ObservabilityGaugeSource(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public async Task<ObservabilitySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();

        // Without platform scope the isolation filter fails closed and every
        // gauge reads zero — which looks exactly like a perfectly idle system.
        scope.ServiceProvider.GetRequiredService<ICustomerScopeAccessor>().SetPlatformScope();

        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        var openIncidents = await context.Incidents
            .AsNoTracking()
            .CountAsync(incident => incident.Status != IncidentStatus.Resolved, cancellationToken);

        var criticalIncidents = await context.Incidents
            .AsNoTracking()
            .CountAsync(
                incident => incident.Status != IncidentStatus.Resolved &&
                            incident.Severity == IncidentSeverity.Critical,
                cancellationToken);

        var queuedJobs = await context.FeatureInstallationJobs
            .AsNoTracking()
            .CountAsync(job => job.State == JobState.Queued, cancellationToken);

        var runningJobs = await context.FeatureInstallationJobs
            .AsNoTracking()
            .CountAsync(job => job.State == JobState.Running, cancellationToken);

        var pendingNotifications = await context.NotificationDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.Status == NotificationDeliveryStatus.Pending, cancellationToken);

        var openAlerts = await context.Alerts
            .AsNoTracking()
            .CountAsync(alert => alert.ResolvedAt == null, cancellationToken);

        var installed = await context.FeatureInstallations
            .AsNoTracking()
            .CountAsync(installation => installation.State == InstallationState.Installed, cancellationToken);

        var failed = await context.FeatureInstallations
            .AsNoTracking()
            .CountAsync(installation => installation.State == InstallationState.Failed, cancellationToken);

        var connected = await context.Stores
            .AsNoTracking()
            .CountAsync(store => store.IntegrationStatus == IntegrationStatus.Connected, cancellationToken);

        var offline = await context.Servers
            .AsNoTracking()
            .CountAsync(server => server.Status == Servers.Domain.ServerStatus.Offline, cancellationToken);

        return new ObservabilitySnapshot(
            openIncidents,
            criticalIncidents,
            queuedJobs,
            runningJobs,
            pendingNotifications,
            openAlerts,
            installed,
            failed,
            connected,
            offline);
    }
}
