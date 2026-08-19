using Knight.Contracts.ControlPlane;
using Servers;
using Servers.Domain;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Turns infrastructure aggregates into the contracts the dashboard reads.
///
/// One rule runs through all of it: an agent's provisioning token and credential
/// have no mapping here, and no response type can carry one. The only place a
/// token appears is the response to the request that created it.
/// </summary>
internal static class ServerMapping
{
    public static ServerResponse ToResponse(this Server server) => new()
    {
        Id = server.Id,
        Name = server.Name,
        HostingModel = server.HostingModel.ToString(),
        Environment = server.Environment.ToString(),
        Status = server.Status.ToString(),
        StatusReason = server.StatusReason,
        Provider = server.Provider,
        Region = server.Region,
        IpAddress = server.IpAddress,
        LastSeenAt = server.LastSeenAt,
        DecommissionedAt = server.DecommissionedAt,
    };

    public static AgentResponse ToResponse(this Agent agent) => new()
    {
        Id = agent.Id,
        ServerId = agent.ServerId,
        Status = agent.Status.ToString(),
        Version = agent.Version,
        LastHeartbeatAt = agent.LastHeartbeatAt,
        EnrolledAt = agent.EnrolledAt,
        RevokedAt = agent.RevokedAt,
        RevokedReason = agent.RevokedReason,
        AwaitingEnrolment = agent.Status is AgentStatus.Provisioning,
    };

    public static ServerMetricResponse ToResponse(this ServerMetric metric) => new(
        metric.CapturedAt,
        metric.CpuPercent,
        metric.MemoryUsedBytes,
        metric.MemoryTotalBytes,
        metric.DiskUsedBytes,
        metric.DiskTotalBytes,
        metric.NetInBytes,
        metric.NetOutBytes,
        metric.LoadAverage,
        metric.MemoryPercent,
        metric.DiskPercent);

    public static AlertResponse ToResponse(this Alert alert) => new()
    {
        Id = alert.Id,
        Source = alert.Source.ToString(),
        SourceId = alert.SourceId,
        CustomerId = alert.CustomerId,
        Severity = alert.Severity.ToString(),
        RuleKey = alert.RuleKey,
        Message = alert.Message,
        RaisedAt = alert.RaisedAt,
        ResolvedAt = alert.ResolvedAt,
        AcknowledgedAt = alert.AcknowledgedAt,
        OccurrenceCount = alert.OccurrenceCount,
        LastObservedAt = alert.LastObservedAt,
        IsOpen = alert.IsOpen,
    };

    public static MonitoringOverviewResponse ToResponse(MonitoringOverview overview) => new(
        overview.TotalServers,
        overview.HealthyServers,
        overview.DegradedServers,
        overview.OfflineServers,
        overview.UnknownServers,
        overview.TotalAgents,
        overview.OnlineAgents,
        overview.OfflineAgents,
        overview.OpenAlerts,
        overview.CriticalAlerts,
        [.. overview.RecentAlerts.Select(ToResponse)],
        [.. overview.Servers.Select(summary => new ServerSummaryResponse(
            summary.Id,
            summary.Name,
            summary.Environment,
            summary.HostingModel,
            summary.Status,
            summary.StatusReason,
            summary.LastSeenAt,
            summary.CpuPercent,
            summary.MemoryPercent,
            summary.DiskPercent))]);
}
