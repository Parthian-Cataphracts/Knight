namespace Knight.Contracts.ControlPlane;

// The customer-facing Automatic Admin surface (docs/adr/0038). The catalogue and
// pricing of the parts are the ordinary catalogue endpoints; these are the engine
// itself — the autonomy setting and the content runs.

/// <summary>The customer's Automatic Admin settings.</summary>
public sealed record AutoAdminSettingsResponse
{
    /// <summary>"ApprovalRequired" (the default) or "FullyAutomatic".</summary>
    public required string Autonomy { get; init; }
}

public sealed record SetAutonomyRequest
{
    /// <summary>"ApprovalRequired" or "FullyAutomatic".</summary>
    public required string Autonomy { get; init; }
}

public sealed record SubmitContentRunRequest
{
    /// <summary>The topic the admin generates content about.</summary>
    public required string Topic { get; init; }
}

/// <summary>One generated piece on a run.</summary>
public sealed record ContentDraftResponse
{
    public required string Kind { get; init; }

    public required string Body { get; init; }

    public required string GeneratorName { get; init; }
}

/// <summary>The result of publishing a run to one channel.</summary>
public sealed record ContentPublicationResponse
{
    public required string ChannelKey { get; init; }

    public required bool Succeeded { get; init; }

    public required string Detail { get; init; }

    public string? ExternalReference { get; init; }

    public required string PublisherName { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }
}

/// <summary>A run of the Automatic Admin — the report it gives back.</summary>
public sealed record ContentRunResponse
{
    public required Guid Id { get; init; }

    public required string Topic { get; init; }

    /// <summary>The autonomy in force when the run started.</summary>
    public required string Autonomy { get; init; }

    /// <summary>"Draft" (awaiting approval), "Published" or "Failed".</summary>
    public required string Status { get; init; }

    /// <summary>True when at least one channel's publish attempt failed.</summary>
    public required bool HasPublicationErrors { get; init; }

    public required IReadOnlyCollection<ContentDraftResponse> Drafts { get; init; }

    public required IReadOnlyCollection<ContentPublicationResponse> Publications { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}
