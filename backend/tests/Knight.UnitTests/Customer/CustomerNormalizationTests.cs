using Customer.Domain;
using Knight.Domain.Exceptions;

namespace Knight.UnitTests.Customer;

public sealed class CustomerNormalizationTests
{
    [Theory]
    [InlineData("+1 (555) 123-4567", "+15551234567")]
    [InlineData("0912-345-6789", "09123456789")]
    [InlineData("  +44 20 7946 0919  ", "+442079460919")]
    [InlineData("555.123.4567", "5551234567")]
    [InlineData("555/123/4567", "5551234567")]
    public void NormalizePhone_ValidPresentations_ProducesCorrectNormalizedDigits(string raw, string expected)
    {
        var (phone, normalized) = CustomerNormalization.NormalizePhone(raw);
        Assert.Equal(raw.Trim(), phone);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("12345")] // Too short (< 7 digits)
    [InlineData("12345678901234567890123")] // Too long (> 20 digits)
    [InlineData("555-CALL-NOW")] // Letters disallowed
    [InlineData("+1+5551234567")] // Extra '+' inside
    public void NormalizePhone_InvalidFormat_ThrowsValidationException(string raw)
    {
        var ex = Assert.Throws<DomainException>(() => CustomerNormalization.NormalizePhone(raw));
        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void NormalizePhone_NullOrWhitespace_ReturnsNull()
    {
        var (p1, n1) = CustomerNormalization.NormalizePhone(null);
        var (p2, n2) = CustomerNormalization.NormalizePhone("   ");

        Assert.Null(p1);
        Assert.Null(n1);
        Assert.Null(p2);
        Assert.Null(n2);
    }

    [Theory]
    [InlineData("user@domain.com", "user@domain.com")]
    [InlineData("  USER@DOMAIN.COM  ", "user@domain.com")]
    [InlineData("first.last+tag@sub.example.co.uk", "first.last+tag@sub.example.co.uk")]
    public void NormalizeEmail_ValidEmail_ProducesTrimmedLowercase(string raw, string expected)
    {
        var (email, normalized) = CustomerNormalization.NormalizeEmail(raw);
        Assert.Equal(raw.Trim(), email);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("plainaddress")]
    [InlineData("@missingusername.com")]
    [InlineData("username@.com")]
    [InlineData("username@domain")]
    public void NormalizeEmail_InvalidFormat_ThrowsValidationException(string raw)
    {
        var ex = Assert.Throws<DomainException>(() => CustomerNormalization.NormalizeEmail(raw));
        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void NormalizeEmail_NullOrWhitespace_ReturnsNull()
    {
        var (e1, n1) = CustomerNormalization.NormalizeEmail(null);
        var (e2, n2) = CustomerNormalization.NormalizeEmail("   ");

        Assert.Null(e1);
        Assert.Null(n1);
        Assert.Null(e2);
        Assert.Null(n2);
    }
}
