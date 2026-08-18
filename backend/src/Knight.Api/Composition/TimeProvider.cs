using Knight.Application.Abstractions.Time;

namespace Knight.Api.Composition;

internal sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
