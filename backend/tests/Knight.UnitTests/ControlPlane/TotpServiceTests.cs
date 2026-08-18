using Knight.Infrastructure.ControlPlane.Security;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class TotpServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private const string ReferenceSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private readonly TotpService _totp = new();

    /// <summary>
    /// RFC 6238 appendix B, the SHA-1 vectors, with the published secret
    /// "12345678901234567890" encoded as base32. Testing against the standard's
    /// own vectors is what makes this an implementation of TOTP rather than of
    /// something that merely resembles it.
    /// </summary>
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void MatchesTheReferenceVectors(long unixSeconds, string expected)
    {
        Assert.True(_totp.Verify(ReferenceSecret, expected, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    [Fact]
    public void GeneratedSecretsAreDistinctAndUsable()
    {
        var first = _totp.GenerateSecret();
        var second = _totp.GenerateSecret();

        Assert.NotEqual(first, second);
        Assert.All(first, character => Assert.Contains(character, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567="));
    }

    [Fact]
    public void ACodeFromTheAdjacentStepIsAccepted()
    {
        // 1111111109 and 1111111111 fall in the same step; 1111111139 is the next
        // one, and one step of drift either way is deliberately tolerated so a
        // code typed as it rolls over still works.
        Assert.True(_totp.Verify(ReferenceSecret, "081804", DateTimeOffset.FromUnixTimeSeconds(1111111139)));
        Assert.True(_totp.Verify(ReferenceSecret, "081804", DateTimeOffset.FromUnixTimeSeconds(1111111079)));
    }

    [Fact]
    public void ACodeTwoStepsAwayIsRejected()
    {
        Assert.False(_totp.Verify(ReferenceSecret, "081804", DateTimeOffset.FromUnixTimeSeconds(1111111169)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void MalformedCodesAreRejectedWithoutThrowing(string code)
    {
        Assert.False(_totp.Verify(_totp.GenerateSecret(), code, Now));
    }

    [Fact]
    public void AMalformedSecretIsRejectedWithoutThrowing()
    {
        Assert.False(_totp.Verify("not base32!", "123456", Now));
    }

    [Fact]
    public void ACodeFromAnotherSecretIsRejected()
    {
        Assert.False(_totp.Verify(_totp.GenerateSecret(), "287082", DateTimeOffset.FromUnixTimeSeconds(59)));
    }

    [Fact]
    public void EnrollmentUriCarriesTheIssuerAndAccount()
    {
        var uri = _totp.BuildEnrollmentUri("ABCDEFGH", "ops@knight.dev", "KNIGHT");

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains("secret=ABCDEFGH", uri);
        Assert.Contains("issuer=KNIGHT", uri);
        Assert.Contains(Uri.EscapeDataString("KNIGHT:ops@knight.dev"), uri);
    }
}
