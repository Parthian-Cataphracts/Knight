using Identity.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Identity;

public sealed class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IssueNewFamily_ForTenantUser_WithoutTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.TenantUser, null, "hash", Now, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void IssueNewFamily_ForPlatformAdmin_WithTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, Guid.NewGuid(), "hash", Now, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void IssueNewFamily_GeneratesAFreshFamilyId()
    {
        var first = RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, null, "hash-a", Now, TimeSpan.FromHours(1));
        var second = RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, null, "hash-b", Now, TimeSpan.FromHours(1));

        Assert.NotEqual(first.FamilyId, second.FamilyId);
    }

    [Fact]
    public void IssueRotated_CarriesOverFamilyIdAndDoesNotExtendExpiration()
    {
        var original = RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, null, "hash-a", Now, TimeSpan.FromHours(1));

        var rotated = RefreshToken.IssueRotated(Guid.NewGuid(), original, "hash-b", Now.AddMinutes(30));

        Assert.Equal(original.FamilyId, rotated.FamilyId);
        Assert.Equal(original.ExpiresAt, rotated.ExpiresAt);
        Assert.Equal(original.SubjectId, rotated.SubjectId);
        Assert.Equal(original.TenantId, rotated.TenantId);
    }

    [Fact]
    public void IsActive_WhenFresh_ReturnsTrue()
    {
        var token = RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, null, "hash", Now, TimeSpan.FromHours(1));

        Assert.True(token.IsActive(Now.AddMinutes(1)));
    }

    [Fact]
    public void IsActive_AfterExpiration_ReturnsFalse()
    {
        var token = RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, null, "hash", Now, TimeSpan.FromHours(1));

        Assert.False(token.IsActive(Now.AddHours(2)));
    }

    [Fact]
    public void IsActive_AfterRevoke_ReturnsFalse()
    {
        var token = RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, null, "hash", Now, TimeSpan.FromHours(1));

        token.Revoke(Now.AddMinutes(1), "test");

        Assert.False(token.IsActive(Now.AddMinutes(2)));
    }

    [Fact]
    public void Revoke_IsIdempotent()
    {
        var token = RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, null, "hash", Now, TimeSpan.FromHours(1));

        token.Revoke(Now.AddMinutes(1), "first");
        token.Revoke(Now.AddMinutes(5), "second");

        Assert.Equal(Now.AddMinutes(1), token.RevokedAt);
        Assert.Equal("first", token.RevokedReason);
    }
}
