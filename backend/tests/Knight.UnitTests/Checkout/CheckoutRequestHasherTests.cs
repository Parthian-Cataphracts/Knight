using Checkout;
using Checkout.Domain;
using Knight.Application.Exceptions;
using Xunit;

namespace Knight.UnitTests.Checkout;

public sealed class CheckoutRequestHasherTests
{
    private readonly CheckoutRequestHasher _hasher = new();

    [Theory]
    [InlineData("12345678")]
    [InlineData("idempotency-key-abc-123")]
    [InlineData("  idempotency-key-abc-123  ")]
    public void ComputeKeyHash_ValidKey_ProducesDeterministic64CharHex(string rawKey)
    {
        var hash1 = _hasher.ComputeKeyHash(rawKey);
        var hash2 = _hasher.ComputeKeyHash(rawKey.Trim());

        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, hash2);
        Assert.Matches("^[0-9a-f]{64}$", hash1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")] // < 8 chars
    public void ComputeKeyHash_InvalidKey_ThrowsValidationException(string? invalidKey)
    {
        Assert.Throws<ValidationException>(() => _hasher.ComputeKeyHash(invalidKey!));
    }

    [Fact]
    public void ComputeKeyHash_ExceedsMaxLength_ThrowsValidationException()
    {
        var tooLong = new string('a', 129);
        Assert.Throws<ValidationException>(() => _hasher.ComputeKeyHash(tooLong));
    }

    [Fact]
    public void ComputeRequestHash_SamePayload_ProducesDeterministicHash()
    {
        var prodId = Guid.NewGuid();
        var mod1 = Guid.NewGuid();
        var mod2 = Guid.NewGuid();

        var party1 = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");
        var party2 = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");

        var items1 = new[] { new CheckoutItemSelection(prodId, null, 2, new[] { mod1, mod2 }) };
        var items2 = new[] { new CheckoutItemSelection(prodId, null, 2, new[] { mod1, mod2 }) };

        var fulfill1 = new CheckoutFulfillmentSelection("Pickup", null, null, null, null, null, null, null);
        var fulfill2 = new CheckoutFulfillmentSelection("Pickup", null, null, null, null, null, null, null);

        var hash1 = _hasher.ComputeRequestHash(party1, items1, fulfill1);
        var hash2 = _hasher.ComputeRequestHash(party2, items2, fulfill2);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
    }

    /// <summary>
    /// Modifier ids are a set of selected choices, not an ordering instruction, so
    /// the canonical request fingerprint sorts them and [A,B] hashes the same as
    /// [B,A].
    ///
    /// That normalization is only sound because it is lossless: the persisted
    /// ordering of <c>OrderItemModifier</c> rows is server-authoritative, derived by
    /// <c>OrderPricingCalculator</c> from Catalog positions rather than from the
    /// client's array order. Both payloads therefore commit byte-identical order
    /// state, which is what makes collapsing them to one fingerprint correct.
    ///
    /// The persistence half of this contract is proven end-to-end against real
    /// PostgreSQL by
    /// <c>CheckoutModifierOrderingTests.ReversedModifierOrder_PersistsIdenticalSnapshotOrdering</c>.
    /// If modifier ordering ever becomes semantically meaningful, this test and that
    /// one must change together.
    /// </summary>
    [Fact]
    public void ComputeRequestHash_ModifierOrdering_IsInvariant()
    {
        var prodId = Guid.NewGuid();
        var mod1 = Guid.NewGuid();
        var mod2 = Guid.NewGuid();

        var party = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");
        var fulfill = new CheckoutFulfillmentSelection("Pickup", null, null, null, null, null, null, null);

        var items1 = new[] { new CheckoutItemSelection(prodId, null, 1, new[] { mod1, mod2 }) };
        var items2 = new[] { new CheckoutItemSelection(prodId, null, 1, new[] { mod2, mod1 }) };

        var hash1 = _hasher.ComputeRequestHash(party, items1, fulfill);
        var hash2 = _hasher.ComputeRequestHash(party, items2, fulfill);

        Assert.Equal(hash1, hash2);
    }

    /// <summary>
    /// The set of selected modifiers *is* semantically relevant, even though its
    /// order is not — swapping one modifier for another must break the fingerprint
    /// so the same key cannot silently commit a different order.
    /// </summary>
    [Fact]
    public void ComputeRequestHash_DifferentModifierSet_ProducesDifferentHash()
    {
        var prodId = Guid.NewGuid();
        var modA = Guid.NewGuid();
        var modB = Guid.NewGuid();
        var modC = Guid.NewGuid();

        var party = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");
        var fulfill = new CheckoutFulfillmentSelection("Pickup", null, null, null, null, null, null, null);

        var items1 = new[] { new CheckoutItemSelection(prodId, null, 1, new[] { modA, modB }) };
        var items2 = new[] { new CheckoutItemSelection(prodId, null, 1, new[] { modA, modC }) };

        var hash1 = _hasher.ComputeRequestHash(party, items1, fulfill);
        var hash2 = _hasher.ComputeRequestHash(party, items2, fulfill);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeRequestHash_DifferentQuantities_ProducesDifferentHash()
    {
        var prodId = Guid.NewGuid();
        var party = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");
        var fulfill = new CheckoutFulfillmentSelection("Pickup", null, null, null, null, null, null, null);

        var items1 = new[] { new CheckoutItemSelection(prodId, null, 1, null) };
        var items2 = new[] { new CheckoutItemSelection(prodId, null, 2, null) };

        var hash1 = _hasher.ComputeRequestHash(party, items1, fulfill);
        var hash2 = _hasher.ComputeRequestHash(party, items2, fulfill);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeRequestHash_DifferentFulfillment_ProducesDifferentHash()
    {
        var prodId = Guid.NewGuid();
        var party = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");

        var items = new[] { new CheckoutItemSelection(prodId, null, 1, null) };

        var fulfillPickup = new CheckoutFulfillmentSelection("Pickup", null, null, null, null, null, null, null);
        var fulfillDelivery = new CheckoutFulfillmentSelection("Delivery", Guid.NewGuid(), "123 Main St", null, "City", "12345", 40.0m, -74.0m);

        var hash1 = _hasher.ComputeRequestHash(party, items, fulfillPickup);
        var hash2 = _hasher.ComputeRequestHash(party, items, fulfillDelivery);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeRequestHash_SubtleCoordinateDifference_ProducesDifferentHash()
    {
        var prodId = Guid.NewGuid();
        var party = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");
        var items = new[] { new CheckoutItemSelection(prodId, null, 1, null) };
        var zoneId = Guid.NewGuid();

        // 35.123451 vs 35.123459 (high precision 6th/7th decimal place)
        var fulfill1 = new CheckoutFulfillmentSelection("Delivery", zoneId, "123 Main St", null, "City", "12345", 35.123451m, 51.123451m);
        var fulfill2 = new CheckoutFulfillmentSelection("Delivery", zoneId, "123 Main St", null, "City", "12345", 35.123459m, 51.123451m);

        var hash1 = _hasher.ComputeRequestHash(party, items, fulfill1);
        var hash2 = _hasher.ComputeRequestHash(party, items, fulfill2);

        Assert.NotEqual(hash1, hash2);
    }

    [Theory]
    [InlineData("John Doe", "Jane Doe")]
    [InlineData("+1234567890", "+1987654321")]
    [InlineData("john@example.com", "jane@example.com")]
    public void ComputeRequestHash_DifferentPartyFields_ProducesDifferentHash(string val1, string val2)
    {
        var prodId = Guid.NewGuid();
        var items = new[] { new CheckoutItemSelection(prodId, null, 1, null) };
        var fulfill = new CheckoutFulfillmentSelection("Pickup", null, null, null, null, null, null, null);

        var party1 = new CheckoutGuestPartySelection(val1, "+1234567890", "john@example.com");
        var party2 = new CheckoutGuestPartySelection(val2.StartsWith("+") ? "John Doe" : val2.Contains('@') ? "John Doe" : val2,
            val2.StartsWith("+") ? val2 : "+1234567890",
            val2.Contains('@') ? val2 : "john@example.com");

        var hash1 = _hasher.ComputeRequestHash(party1, items, fulfill);
        var hash2 = _hasher.ComputeRequestHash(party2, items, fulfill);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeRequestHash_DifferentAddressFields_ProducesDifferentHash()
    {
        var prodId = Guid.NewGuid();
        var party = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");
        var items = new[] { new CheckoutItemSelection(prodId, null, 1, null) };
        var zoneId = Guid.NewGuid();

        var f1 = new CheckoutFulfillmentSelection("Delivery", zoneId, "123 Main St", "Apt 1", "City", "12345", null, null);
        var f2 = new CheckoutFulfillmentSelection("Delivery", zoneId, "124 Main St", "Apt 1", "City", "12345", null, null);
        var f3 = new CheckoutFulfillmentSelection("Delivery", zoneId, "123 Main St", "Apt 2", "City", "12345", null, null);
        var f4 = new CheckoutFulfillmentSelection("Delivery", zoneId, "123 Main St", "Apt 1", "Town", "12345", null, null);
        var f5 = new CheckoutFulfillmentSelection("Delivery", zoneId, "123 Main St", "Apt 1", "City", "54321", null, null);

        var h1 = _hasher.ComputeRequestHash(party, items, f1);
        var h2 = _hasher.ComputeRequestHash(party, items, f2);
        var h3 = _hasher.ComputeRequestHash(party, items, f3);
        var h4 = _hasher.ComputeRequestHash(party, items, f4);
        var h5 = _hasher.ComputeRequestHash(party, items, f5);

        Assert.NotEqual(h1, h2);
        Assert.NotEqual(h1, h3);
        Assert.NotEqual(h1, h4);
        Assert.NotEqual(h1, h5);
    }

    [Fact]
    public void ComputeRequestHash_DifferentCouponCodes_ProducesDifferentHash()
    {
        var prodId = Guid.NewGuid();
        var party = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");
        var items = new[] { new CheckoutItemSelection(prodId, null, 1, null) };
        var fulfill = new CheckoutFulfillmentSelection("Pickup", null, null, null, null, null, null, null);

        var hash1 = _hasher.ComputeRequestHash(party, items, fulfill, "SAVE10");
        var hash2 = _hasher.ComputeRequestHash(party, items, fulfill, "SAVE20");
        var hashNoCoupon = _hasher.ComputeRequestHash(party, items, fulfill, null);

        Assert.NotEqual(hash1, hash2);
        Assert.NotEqual(hash1, hashNoCoupon);
        Assert.NotEqual(hash2, hashNoCoupon);
    }

    [Fact]
    public void ComputeRequestHash_NormalizedCouponCodes_ProducesIdenticalHash()
    {
        var prodId = Guid.NewGuid();
        var party = new CheckoutGuestPartySelection("John Doe", "+1234567890", "john@example.com");
        var items = new[] { new CheckoutItemSelection(prodId, null, 1, null) };
        var fulfill = new CheckoutFulfillmentSelection("Pickup", null, null, null, null, null, null, null);

        var hash1 = _hasher.ComputeRequestHash(party, items, fulfill, "save10");
        var hash2 = _hasher.ComputeRequestHash(party, items, fulfill, "  SAVE10  ");
        var hash3 = _hasher.ComputeRequestHash(party, items, fulfill, "Save10");

        Assert.Equal(hash1, hash2);
        Assert.Equal(hash1, hash3);
    }
}
