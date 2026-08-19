namespace Knight.Contracts.ControlPlane;

/// <summary>
/// One problem, as the errors screen reads it (docs/api-contracts.md §2).
///
/// The counters and the two "seen in version" fields are the whole point: they
/// are what turns a list of exceptions into a list of things to fix, in order.
/// </summary>
public sealed record ErrorGroupResponse
{
    public required Guid Id { get; init; }

    public required Guid StoreId { get; init; }

    public string? StoreName { get; init; }

    public required string Environment { get; init; }

    public required string ExceptionType { get; init; }

    public required string Title { get; init; }

    public string? Endpoint { get; init; }

    public required long OccurrenceCount { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset FirstSeenAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }

    public string? FirstSeenVersion { get; init; }

    public string? LastSeenVersion { get; init; }

    /// <summary>True when this was resolved and came back. Shown prominently, because it means a fix did not hold.</summary>
    public required bool IsRegression { get; init; }

    public Guid? IncidentId { get; init; }
}

/// <summary>
/// One kept occurrence. Only sampled events carry a stack trace; the rest were
/// stripped on ingest and exist as counters.
/// </summary>
public sealed record ErrorEventSampleResponse
{
    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Version { get; init; }

    public string? RequestId { get; init; }

    public string? TraceId { get; init; }

    public string? StackTrace { get; init; }

    public required string Message { get; init; }

    public string? Endpoint { get; init; }

    public int? StatusCode { get; init; }
}

public sealed record ResolveErrorGroupRequest
{
    /// <summary>The version believed to contain the fix. Optional; the last seen version is assumed.</summary>
    public string? InVersion { get; init; }
}

public sealed record IncidentResponse
{
    public required Guid Id { get; init; }

    public required string Reference { get; init; }

    public required string Title { get; init; }

    public string? Summary { get; init; }

    public required string Severity { get; init; }

    public required string Status { get; init; }

    public Guid? CustomerId { get; init; }

    public Guid? StoreId { get; init; }

    public string? StoreName { get; init; }

    public Guid? ServerId { get; init; }

    public string? ServerName { get; init; }

    /// <summary>The rule that opened it, or null when a person did.</summary>
    public string? RuleKey { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    public DateTimeOffset? AcknowledgedAt { get; init; }

    public DateTimeOffset? MitigatedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public string? RootCause { get; init; }
}

public sealed record IncidentEventResponse
{
    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string Type { get; init; }

    /// <summary>The person who did it, or "System" when a rule did.</summary>
    public required string Actor { get; init; }

    public required string Message { get; init; }
}

public sealed record OpenIncidentRequest
{
    public required string Title { get; init; }

    /// <summary>Info, Warning or Critical.</summary>
    public required string Severity { get; init; }

    public string? Summary { get; init; }

    public Guid? CustomerId { get; init; }

    public Guid? StoreId { get; init; }

    public Guid? ServerId { get; init; }
}

public sealed record IncidentNoteRequest
{
    public required string Message { get; init; }
}

public sealed record ResolveIncidentRequest
{
    public string? RootCause { get; init; }
}

public sealed record ReopenIncidentRequest
{
    public required string Reason { get; init; }
}

public sealed record NotificationChannelResponse
{
    public required Guid Id { get; init; }

    public Guid? CustomerId { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public string? Endpoint { get; init; }

    public required string MinimumSeverity { get; init; }

    public required IReadOnlyCollection<string> RuleFilter { get; init; }

    public required bool IsEnabled { get; init; }

    public string? DisabledReason { get; init; }

    public DateTimeOffset? LastDeliveredAt { get; init; }

    public required int ConsecutiveFailures { get; init; }

    /// <summary>
    /// Whether a signing secret is configured. The secret itself is never
    /// returned — not to the customer who set it, not to platform staff, not
    /// once.
    /// </summary>
    public required bool HasSecret { get; init; }
}

public sealed record CreateNotificationChannelRequest
{
    public required string Name { get; init; }

    /// <summary>InApp, Email or Webhook.</summary>
    public required string Kind { get; init; }

    /// <summary>An address or URL. Omitted for the in-app kind, which has no destination outside KNIGHT.</summary>
    public string? Endpoint { get; init; }

    public required string MinimumSeverity { get; init; }

    /// <summary>Rule keys this channel wants, or empty for all of them at or above the severity floor.</summary>
    public IReadOnlyCollection<string>? RuleFilter { get; init; }

    /// <summary>The shared secret webhook payloads are signed with. Write-only.</summary>
    public string? Secret { get; init; }

    /// <summary>The customer this channel belongs to, or null for a platform channel. Platform staff only.</summary>
    public Guid? CustomerId { get; init; }
}

public sealed record UpdateNotificationChannelRequest
{
    public required string Name { get; init; }

    public required string MinimumSeverity { get; init; }

    public IReadOnlyCollection<string>? RuleFilter { get; init; }
}

public sealed record NotificationDeliveryResponse
{
    public required Guid Id { get; init; }

    public required Guid ChannelId { get; init; }

    public string? ChannelName { get; init; }

    public required string Severity { get; init; }

    public required string RuleKey { get; init; }

    public required string Subject { get; init; }

    public required Guid SubjectId { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public required string Status { get; init; }

    public required int AttemptCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    public DateTimeOffset? ReadAt { get; init; }

    public string? LastError { get; init; }
}

/// <summary>The outcome of a channel test, reported to the operator who pressed the button.</summary>
public sealed record NotificationTestResponse
{
    public required bool Succeeded { get; init; }

    public string? Error { get; init; }
}

// --- Dashboard summary panels ------------------------------------------------

/// <summary>
/// One platform dependency on the infrastructure screen. <see cref="Metrics"/>
/// is a list of two-element [label, value] pairs, because what is worth showing
/// differs per service and a typed shape would fit none of them.
/// </summary>
public sealed record PlatformServiceResponse
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string Detail { get; init; }

    /// <summary>Healthy, Degraded, Offline or Unknown.</summary>
    public required string Status { get; init; }

    public required IReadOnlyCollection<string[]> Metrics { get; init; }
}

public sealed record ReportSummaryResponse
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>When the data behind this report last changed. Null when there is none yet.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record EntitlementMatrixRowResponse
{
    public required string FeatureSlug { get; init; }

    public required string FeatureName { get; init; }

    /// <summary>Keyed by plan key: "yes", a pinned version range, or "—" when the plan does not include it.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }
}

public sealed record ActivityItemResponse
{
    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>user, system, warning or event — what tone the feed shows it in.</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }

    public required string Actor { get; init; }
}

public sealed record CustomerNoteResponse
{
    public required Guid Id { get; init; }

    public required string Author { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string Body { get; init; }
}

public sealed record CreateCustomerNoteRequest
{
    public required string Body { get; init; }
}

/// <summary>
/// What a store has actually been doing, hour by hour.
///
/// There is deliberately no request count and no storage figure: stores report
/// neither, and a dashboard showing an invented number would be worse than one
/// showing fewer real ones.
/// </summary>
public sealed record StoreUsageResponse
{
    public required IReadOnlyList<int> Errors { get; init; }

    public required IReadOnlyList<int> Logs { get; init; }

    public required IReadOnlyList<int> HealthLatencyMs { get; init; }

    public required int WindowHours { get; init; }

    public required long TotalErrors { get; init; }

    public required long TotalLogs { get; init; }
}
