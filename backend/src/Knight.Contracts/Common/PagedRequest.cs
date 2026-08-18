namespace Knight.Contracts.Common;

public sealed record PagedRequest
{
    private const int MaxPageSize = 100;

    public int Page { get; init; } = 1;

    private readonly int _pageSize = 20;

    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = Math.Clamp(value, 1, MaxPageSize);
    }

    public string? SortBy { get; init; }

    public bool SortDescending { get; init; }
}
