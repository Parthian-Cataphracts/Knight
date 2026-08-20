namespace Knight.Contracts.ControlPlane;

// --- Dashboard requests -------------------------------------------------------

/// <summary>
/// Starts a provisioning or deprovisioning run for a store. The idempotency key
/// is optional: without one the store and the kind of run are the key, which is
/// the behaviour a double-clicked button wants.
/// </summary>
public sealed record StartProvisioningRequest(string? IdempotencyKey);

/// <summary>Records that a person did what only a person can do — built the machine, wired DNS, produced the export.</summary>
public sealed record CompleteProvisioningStepRequest(string Step, string? Detail);

public sealed record CancelProvisioningRequest(string Reason);

/// <summary>
/// A negotiated retention window in days, or null to fall back to the plan's.
/// Set on the customer, because it is a contractual promise to that customer.
/// </summary>
public sealed record SetRetentionOverrideRequest(int? Days);

// --- Dashboard responses ------------------------------------------------------

public sealed record ProvisioningStepResponse
{
    public required int Sequence { get; init; }

    public required string Name { get; init; }

    /// <summary>Automatic or Manual. The dashboard shows a manual step as something to do, not something to wait for.</summary>
    public required string Mode { get; init; }

    public required string Status { get; init; }

    /// <summary>What the step is waiting for, or what it did. Written for a person to act on.</summary>
    public string? Detail { get; init; }

    public string? ErrorCode { get; init; }

    public Guid? CompletedBy { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record ProvisioningJobResponse
{
    public required Guid Id { get; init; }

    public required Guid StoreId { get; init; }

    public required Guid CustomerId { get; init; }

    /// <summary>Provision or Deprovision.</summary>
    public required string Kind { get; init; }

    public required string State { get; init; }

    /// <summary>True while the run is sitting on a step only a person can finish.</summary>
    public required bool AwaitingOperator { get; init; }

    /// <summary>The step the run is on, or null when it has finished.</summary>
    public string? CurrentStep { get; init; }

    public required int CompletedStepCount { get; init; }

    public required int TotalStepCount { get; init; }

    public string? BaseImageVersion { get; init; }

    /// <summary>When a deprovisioned store's data may be purged. Null on a provisioning run.</summary>
    public DateTimeOffset? RetainUntil { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required IReadOnlyList<ProvisioningStepResponse> Steps { get; init; }
}

/// <summary>
/// One backup a store reported. No credential and no download link: KNIGHT never
/// holds the backup, and the location is a reference an operator resolves in
/// whatever system actually stores it.
/// </summary>
public sealed record StoreBackupResponse
{
    public required Guid Id { get; init; }

    public required Guid StoreId { get; init; }

    public required string Status { get; init; }

    public required string Kind { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required DateTimeOffset ReportedAt { get; init; }

    public long? SizeBytes { get; init; }

    public string? Location { get; init; }

    public string? Detail { get; init; }

    public int? DurationSeconds { get; init; }
}
