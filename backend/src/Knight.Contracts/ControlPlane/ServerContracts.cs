namespace Knight.Contracts.ControlPlane;

// --- Dashboard requests -------------------------------------------------------

public sealed record RegisterServerRequest(
    string Name,
    string HostingModel,
    string Environment,
    string? Provider,
    string? Region,
    string? IpAddress);

public sealed record UpdateServerRequest(string Name, string? Provider, string? Region, string? IpAddress);

public sealed record RevokeAgentRequest(string Reason);

// --- Dashboard responses ------------------------------------------------------

public sealed record ServerResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string HostingModel { get; init; }

    public required string Environment { get; init; }

    public required string Status { get; init; }

    /// <summary>Why the server is in this status, in words. Null when it is simply healthy.</summary>
    public string? StatusReason { get; init; }

    public string? Provider { get; init; }

    public string? Region { get; init; }

    public string? IpAddress { get; init; }

    public DateTimeOffset? LastSeenAt { get; init; }

    public DateTimeOffset? DecommissionedAt { get; init; }
}

/// <summary>
/// An agent as the dashboard sees it.
///
/// Neither the provisioning token nor the credential appears here, and no
/// response type in this file can carry one. That is what makes "an agent secret
/// is never returned by a read API" true by construction rather than by every
/// read path remembering.
/// </summary>
public sealed record AgentResponse
{
    public required Guid Id { get; init; }

    public required Guid ServerId { get; init; }

    public required string Status { get; init; }

    public string? Version { get; init; }

    public DateTimeOffset? LastHeartbeatAt { get; init; }

    public DateTimeOffset? EnrolledAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public string? RevokedReason { get; init; }

    /// <summary>True while the one-time provisioning token can still be presented.</summary>
    public required bool AwaitingEnrolment { get; init; }
}

/// <summary>
/// The one and only time a provisioning token is shown. It is not stored in
/// plaintext anywhere, so an operator who loses it issues a new one rather than
/// looking the old one up.
/// </summary>
public sealed record ProvisioningTokenResponse(
    Guid AgentId,
    Guid ServerId,
    string Token,
    DateTimeOffset ExpiresAt);

public sealed record ServerMetricResponse(
    DateTimeOffset CapturedAt,
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskUsedBytes,
    long DiskTotalBytes,
    long NetInBytes,
    long NetOutBytes,
    double? LoadAverage,
    double? MemoryPercent,
    double? DiskPercent);

public sealed record AlertResponse
{
    public required Guid Id { get; init; }

    public required string Source { get; init; }

    public required Guid SourceId { get; init; }

    public Guid? CustomerId { get; init; }

    public required string Severity { get; init; }

    public required string RuleKey { get; init; }

    public required string Message { get; init; }

    public required DateTimeOffset RaisedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public DateTimeOffset? AcknowledgedAt { get; init; }

    /// <summary>How many times the condition has been seen. One long outage, not seven hundred rows.</summary>
    public required int OccurrenceCount { get; init; }

    public required DateTimeOffset LastObservedAt { get; init; }

    public required bool IsOpen { get; init; }
}

public sealed record ServerSummaryResponse(
    Guid Id,
    string Name,
    string Environment,
    string HostingModel,
    string Status,
    string? StatusReason,
    DateTimeOffset? LastSeenAt,
    double? CpuPercent,
    double? MemoryPercent,
    double? DiskPercent);

public sealed record MonitoringOverviewResponse(
    int TotalServers,
    int HealthyServers,
    int DegradedServers,
    int OfflineServers,
    int UnknownServers,
    int TotalAgents,
    int OnlineAgents,
    int OfflineAgents,
    int OpenAlerts,
    int CriticalAlerts,
    IReadOnlyList<AlertResponse> RecentAlerts,
    IReadOnlyList<ServerSummaryResponse> Servers);

// --- The agent channel --------------------------------------------------------

public sealed record AgentEnrolRequest(string ProvisioningToken, string? Version, string? Capabilities);

public sealed record AgentEnrolResponse(
    Guid AgentId,
    Guid ServerId,
    string Credential,
    int HeartbeatIntervalSeconds);

public sealed record AgentMetricsPayload(
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskUsedBytes,
    long DiskTotalBytes,
    long NetInBytes,
    long NetOutBytes,
    double? LoadAverage);

public sealed record AgentHeartbeatRequest(string? Version, string? Capabilities, AgentMetricsPayload? Metrics);

/// <summary>
/// The heartbeat's answer. The interval comes from KNIGHT rather than the agent
/// choosing one, so an agent cannot quietly stop being monitored by deciding to
/// report once a day.
/// </summary>
public sealed record AgentHeartbeatResponse(int HeartbeatIntervalSeconds, string ServerStatus);
