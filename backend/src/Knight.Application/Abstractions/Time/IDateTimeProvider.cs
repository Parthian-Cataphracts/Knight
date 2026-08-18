namespace Knight.Application.Abstractions.Time;

/// <summary>
/// Testable source of the current instant. Application and domain code must not
/// call <see cref="DateTimeOffset.UtcNow"/> directly.
/// </summary>
public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}
