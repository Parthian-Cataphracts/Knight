using Checkout.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Checkout;

public sealed class CheckoutDomainTests
{
    [Fact]
    public void CreateClaim_ValidParameters_CreatesRecord()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var keyHash = new string('a', 64);
        var reqHash = new string('b', 64);
        var now = DateTimeOffset.UtcNow;

        var record = CheckoutIdempotencyRecord.CreateClaim(id, tenantId, keyHash, reqHash, now);

        Assert.Equal(id, record.Id);
        Assert.Equal(tenantId, record.TenantId);
        Assert.Equal(keyHash, record.KeyHash);
        Assert.Equal(reqHash, record.RequestHash);
        Assert.Equal(now, record.CreatedAt);
        Assert.False(record.IsCompleted);
        Assert.Null(record.OrderId);
        Assert.Null(record.CompletedAt);
    }

    [Fact]
    public void Complete_ValidOrderId_MarksCompleted()
    {
        var record = CheckoutIdempotencyRecord.CreateClaim(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', 64),
            new string('b', 64),
            DateTimeOffset.UtcNow);

        var orderId = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow;

        record.Complete(orderId, completedAt);

        Assert.True(record.IsCompleted);
        Assert.Equal(orderId, record.OrderId);
        Assert.Equal(completedAt, record.CompletedAt);
    }

    [Fact]
    public void Complete_EmptyOrderId_ThrowsDomainException()
    {
        var record = CheckoutIdempotencyRecord.CreateClaim(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', 64),
            new string('b', 64),
            DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => record.Complete(Guid.Empty, DateTimeOffset.UtcNow));
    }
}
