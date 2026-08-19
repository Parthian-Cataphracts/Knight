using Knight.Domain.Versioning;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Ranges are what a manifest actually writes down, so the cases here are the
/// ones manifests actually contain — including the partial versions Python and
/// Django are named with.
/// </summary>
public sealed class VersionRangeTests
{
    private static bool Includes(string range, string version) =>
        VersionRange.Parse(range).Includes(SemanticVersion.Parse(version));

    [Theory]
    [InlineData(">=4.0.0,<6.0.0", "4.0.0", true)]
    [InlineData(">=4.0.0,<6.0.0", "5.9.9", true)]
    [InlineData(">=4.0.0,<6.0.0", "6.0.0", false)]
    [InlineData(">=4.0.0,<6.0.0", "3.9.9", false)]
    [InlineData(">1.0.0", "1.0.1", true)]
    [InlineData(">1.0.0", "1.0.0", false)]
    [InlineData("<=2.0.0", "2.0.0", true)]
    [InlineData("!=1.5.0", "1.5.0", false)]
    [InlineData("!=1.5.0", "1.5.1", true)]
    public void Comparators_BoundWhereTheySay(string range, string version, bool expected)
    {
        Assert.Equal(expected, Includes(range, version));
    }

    [Fact]
    public void ABareVersion_PinsRatherThanMeaningAtLeast()
    {
        Assert.True(Includes("1.4.0", "1.4.0"));
        Assert.False(Includes("1.4.0", "1.4.1"));
        Assert.False(Includes("1.4.0", "1.3.9"));
    }

    [Theory]
    [InlineData(">=3.12", "3.12.0", true)]
    [InlineData(">=3.12", "3.11.9", false)]
    [InlineData(">=3.12", "3.13.1", true)]
    [InlineData(">=5.0,<6.0", "5.1.4", true)]
    [InlineData(">=5.0,<6.0", "6.0.0", false)]
    [InlineData(">=5.0,<6.0", "4.2.0", false)]
    public void PartialOperands_ArePaddedToTheBoundaryTheAuthorMeant(string range, string version, bool expected)
    {
        // "<6.0" must exclude every 6.x, and ">=5.0" must admit every 5.x.
        Assert.Equal(expected, Includes(range, version));
    }

    [Fact]
    public void Star_AdmitsEveryStableVersion()
    {
        Assert.True(VersionRange.Any.IsUnbounded);
        Assert.True(Includes("*", "0.0.1"));
        Assert.True(Includes("*", "99.0.0"));
    }

    [Fact]
    public void APreRelease_NeverSatisfiesARangeThatDoesNotAskForOne()
    {
        // Otherwise publishing 2.0.0-rc.1 would immediately start shipping it to
        // every store whose plan says ">=1.0.0".
        Assert.False(Includes(">=1.0.0", "2.0.0-rc.1"));
        Assert.False(Includes("*", "1.0.0-rc.1"));
        Assert.False(Includes(">=1.0.0,<3.0.0", "2.0.0-rc.1"));
    }

    [Fact]
    public void APreRelease_SatisfiesARangeThatNamesItsOwnRelease()
    {
        Assert.True(Includes(">=2.0.0-rc.1", "2.0.0-rc.1"));
        Assert.True(Includes(">=2.0.0-rc.1,<3.0.0", "2.0.0-rc.2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(">=")]
    [InlineData(">=1.0.0,")]
    [InlineData("~>1.0.0")]
    [InlineData("1.0.0 || 2.0.0")]
    [InlineData("1.0.0.0")]
    public void MalformedRanges_AreRefused(string text)
    {
        Assert.False(VersionRange.TryParse(text, out _));
    }

    [Fact]
    public void BestMatch_TakesTheHighestSatisfyingCandidate()
    {
        var candidates = new[] { "1.0.0", "1.2.0", "1.9.0", "2.0.0" }.Select(SemanticVersion.Parse).ToList();

        var best = VersionRange.Parse(">=1.0.0,<2.0.0").BestMatch(candidates);

        Assert.Equal("1.9.0", best?.ToString());
    }

    [Fact]
    public void BestMatch_IsNullWhenNothingSatisfies()
    {
        var candidates = new[] { "1.0.0", "1.2.0" }.Select(SemanticVersion.Parse).ToList();

        Assert.Null(VersionRange.Parse(">=3.0.0").BestMatch(candidates));
    }

    [Fact]
    public void Expression_IsPreservedSoAnErrorCanQuoteTheAuthor()
    {
        Assert.Equal(">=4.0.0,<6.0.0", VersionRange.Parse(" >=4.0.0,<6.0.0 ").ToString());
    }
}
