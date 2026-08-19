using FeatureRegistry.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Version comparison decides whether a delivery is an upgrade, a downgrade or a
/// no-op, so it is tested against the semver specification's own examples rather
/// than against what looks reasonable.
/// </summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.4.0", 1, 4, 0, null)]
    [InlineData("0.0.1", 0, 0, 1, null)]
    [InlineData("10.20.30", 10, 20, 30, null)]
    [InlineData("1.0.0-rc.1", 1, 0, 0, "rc.1")]
    [InlineData("1.0.0-alpha", 1, 0, 0, "alpha")]
    [InlineData("  2.1.0  ", 2, 1, 0, null)]
    public void Parse_ReadsEveryComponent(string text, int major, int minor, int patch, string? preRelease)
    {
        var version = SemanticVersion.Parse(text);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(preRelease, version.PreRelease);
    }

    [Fact]
    public void Parse_DiscardsBuildMetadata()
    {
        // Semver §10: build metadata is not part of identity. Two artifacts that
        // differ only there are the same release, and publishing both must
        // collide rather than produce two installable versions.
        var left = SemanticVersion.Parse("1.0.0+build.1");
        var right = SemanticVersion.Parse("1.0.0+build.2");

        Assert.Equal("1.0.0", left.ToString());
        Assert.Equal(0, left.CompareTo(right));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.01.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.x")]
    [InlineData("-1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-rc..1")]
    [InlineData("1.0.0-rc!")]
    [InlineData("1.0.0+")]
    public void Parse_RefusesMalformedVersions(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
        Assert.Throws<DomainException>(() => SemanticVersion.Parse(text));
    }

    [Fact]
    public void LeadingZeroes_AreRefusedSoOneVersionHasOneSpelling()
    {
        // The registry's uniqueness constraint is on the stored text, so two
        // spellings of one version would be two rows nobody could tell apart.
        Assert.False(SemanticVersion.TryParse("1.01.0", out _));
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("2.0.0", "2.1.0")]
    [InlineData("2.1.0", "2.1.1")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void Ordering_FollowsTheSpecificationsOwnExample(string lower, string higher)
    {
        var left = SemanticVersion.Parse(lower);
        var right = SemanticVersion.Parse(higher);

        Assert.True(left < right, $"{lower} should precede {higher}");
        Assert.True(right > left);
        Assert.False(left >= right);
    }

    [Fact]
    public void NumericPreReleaseIdentifiers_CompareNumericallyNotAsText()
    {
        // The case string comparison gets wrong: "11" sorts before "2" as text.
        Assert.True(SemanticVersion.Parse("1.0.0-beta.2") < SemanticVersion.Parse("1.0.0-beta.11"));
    }

    [Fact]
    public void Equality_IgnoresNothingElse()
    {
        Assert.Equal(SemanticVersion.Parse("1.2.3"), SemanticVersion.Parse("1.2.3"));
        Assert.NotEqual(SemanticVersion.Parse("1.2.3"), SemanticVersion.Parse("1.2.3-rc.1"));
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        foreach (var text in new[] { "1.2.3", "0.0.1", "1.0.0-rc.1", "10.20.30-alpha.1" })
        {
            Assert.Equal(text, SemanticVersion.Parse(text).ToString());
        }
    }
}
