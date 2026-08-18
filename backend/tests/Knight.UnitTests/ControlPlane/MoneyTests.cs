using Knight.Domain.Common;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class MoneyTests
{
    [Fact]
    public void AmountsAreRoundedToTheCurrencyScale()
    {
        Assert.Equal(10.34m, Money.Of(10.344m, "EUR").Amount);
        Assert.Equal(10.34m, Money.Of(10.335m, "EUR").Amount);
    }

    [Theory]
    [InlineData("eur", "EUR")]
    [InlineData(" usd ", "USD")]
    public void CurrencyIsNormalized(string input, string expected)
    {
        Assert.Equal(expected, Money.Of(1m, input).Currency);
    }

    [Theory]
    [InlineData("EURO")]
    [InlineData("E")]
    [InlineData("12E")]
    [InlineData("")]
    public void AnInvalidCurrencyIsRefused(string currency)
    {
        Assert.Throws<DomainException>(() => Money.Of(1m, currency));
    }

    [Fact]
    public void ANegativeAmountIsRefused()
    {
        Assert.Throws<DomainException>(() => Money.Of(-0.01m, "EUR"));
    }

    [Fact]
    public void AddingAcrossCurrenciesIsRefusedRatherThanPerformed()
    {
        var euros = Money.Of(10m, "EUR");
        var dollars = Money.Of(10m, "USD");

        var exception = Assert.Throws<DomainException>(() => euros.Add(dollars));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void AddingKeepsTheCurrency()
    {
        var total = Money.Of(10.01m, "EUR").Add(Money.Of(2.02m, "EUR"));

        Assert.Equal(12.03m, total.Amount);
        Assert.Equal("EUR", total.Currency);
    }

    [Fact]
    public void MultiplyingScalesTheAmount()
    {
        Assert.Equal(30m, Money.Of(10m, "EUR").Multiply(3).Amount);
        Assert.True(Money.Of(10m, "EUR").Multiply(0).IsZero);
    }

    [Fact]
    public void ProratingSplitsAcrossThePeriod()
    {
        var monthly = Money.Of(30m, "EUR");

        Assert.Equal(10m, monthly.Prorate(10, 30).Amount);
        Assert.Equal(30m, monthly.Prorate(30, 30).Amount);
        Assert.True(monthly.Prorate(0, 30).IsZero);
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(31, 30)]
    [InlineData(1, 0)]
    public void ProratingOutsideThePeriodIsRefused(int elapsed, int total)
    {
        Assert.Throws<DomainException>(() => Money.Of(30m, "EUR").Prorate(elapsed, total));
    }

    [Fact]
    public void EqualityIsByValueIncludingCurrency()
    {
        Assert.Equal(Money.Of(10m, "EUR"), Money.Of(10m, "EUR"));
        Assert.NotEqual(Money.Of(10m, "EUR"), Money.Of(10m, "USD"));
    }
}
