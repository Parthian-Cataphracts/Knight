namespace Knight.Contracts.Ordering;

public sealed record TransitionOrderStatusRequest
{
    public required string TargetStatus { get; init; }
    public string? Reason { get; init; }
}

public sealed record CancelOrderRequest
{
    public string? Reason { get; init; }
}
