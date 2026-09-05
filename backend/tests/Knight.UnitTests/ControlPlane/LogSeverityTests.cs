using Ingestion.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The severity ladder that separates the errors, warnings and alerts from the
/// noise (docs/risks.md §3.4). Stores log in their own vocabulary, so the ladder
/// has to map many raw tokens onto a few ranks and answer "at or above" across
/// all of them.
/// </summary>
public sealed class LogSeverityTests
{
    [Fact]
    public void AtOrAboveWarning_KeepsWarningsErrorsAndAlerts_AndDropsTheRest()
    {
        var tokens = LogSeverity.TokensAtOrAbove("Warning");

        Assert.NotNull(tokens);

        // The whole vocabulary of the problem levels, whatever a store called them.
        Assert.Contains("WARN", tokens);
        Assert.Contains("WARNING", tokens);
        Assert.Contains("ERROR", tokens);
        Assert.Contains("ERR", tokens);
        Assert.Contains("CRITICAL", tokens);
        Assert.Contains("FATAL", tokens);
        Assert.Contains("ALERT", tokens);

        // The noise below the line is gone.
        Assert.DoesNotContain("INFO", tokens);
        Assert.DoesNotContain("INFORMATION", tokens);
        Assert.DoesNotContain("DEBUG", tokens);
        Assert.DoesNotContain("TRACE", tokens);
    }

    [Fact]
    public void AtOrAboveError_KeepsOnlyErrorsAndAlerts()
    {
        var tokens = LogSeverity.TokensAtOrAbove("Error");

        Assert.NotNull(tokens);
        Assert.Contains("ERROR", tokens);
        Assert.Contains("CRITICAL", tokens);
        Assert.DoesNotContain("WARN", tokens);
        Assert.DoesNotContain("WARNING", tokens);
    }

    [Fact]
    public void TheMinimumIsCaseInsensitiveAndAcceptsARawToken()
    {
        // "err" is a raw token, not a canonical name, and still resolves to Error.
        Assert.Equal(
            LogSeverity.TokensAtOrAbove("Error")!.OrderBy(t => t),
            LogSeverity.TokensAtOrAbove("err")!.OrderBy(t => t));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-level")]
    public void AnUnrecognisedOrMissingMinimum_IsNull_SoTheFilterIsIgnored(string? minimum)
    {
        Assert.Null(LogSeverity.TokensAtOrAbove(minimum));
    }
}
