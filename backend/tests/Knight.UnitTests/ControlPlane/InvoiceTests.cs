using Billing.Domain;
using Knight.Domain.Common;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class InvoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.NewGuid();

    private static Invoice Draft() =>
        Invoice.Draft(Guid.NewGuid(), Now, CustomerId, Guid.NewGuid(), Now, Now.AddDays(30), "EUR");

    private static Invoice Issued()
    {
        var invoice = Draft();
        invoice.AddLine("Basic plan", null, 1, Money.Of(49m, "EUR"), Now);
        invoice.Issue("2026-000001", Now, Now.AddDays(14));
        return invoice;
    }

    [Fact]
    public void TotalsFollowTheLines()
    {
        var invoice = Draft();

        invoice.AddLine("Basic plan", null, 1, Money.Of(49m, "EUR"), Now);
        invoice.AddLine("Analytics", Guid.NewGuid(), 2, Money.Of(29m, "EUR"), Now);

        Assert.Equal(107m, invoice.Subtotal);
        Assert.Equal(107m, invoice.Total);
    }

    [Fact]
    public void TaxIsAddedToTheTotalButNotToTheSubtotal()
    {
        var invoice = Draft();
        invoice.AddLine("Basic plan", null, 1, Money.Of(100m, "EUR"), Now);

        invoice.SetTax(Money.Of(21m, "EUR"), Now);

        Assert.Equal(100m, invoice.Subtotal);
        Assert.Equal(21m, invoice.Tax);
        Assert.Equal(121m, invoice.Total);
    }

    [Fact]
    public void ALineInAnotherCurrencyIsRefused()
    {
        var invoice = Draft();

        var exception = Assert.Throws<DomainException>(() =>
            invoice.AddLine("Basic plan", null, 1, Money.Of(49m, "USD"), Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void AnEmptyInvoiceCannotBeIssued()
    {
        var invoice = Draft();

        Assert.Throws<DomainException>(() => invoice.Issue("2026-000001", Now, Now.AddDays(14)));
    }

    [Fact]
    public void AnIssuedInvoiceIsFrozen()
    {
        var invoice = Issued();

        // Correcting an issued invoice means voiding and reissuing, never editing.
        Assert.Throws<DomainException>(() => invoice.AddLine("Extra", null, 1, Money.Of(10m, "EUR"), Now));
        Assert.Throws<DomainException>(() => invoice.SetTax(Money.Of(1m, "EUR"), Now));
        Assert.Throws<DomainException>(() => invoice.ClearLines(Now));
    }

    [Fact]
    public void ADraftCannotBePaid()
    {
        var invoice = Draft();
        invoice.AddLine("Basic plan", null, 1, Money.Of(49m, "EUR"), Now);

        Assert.Throws<DomainException>(() =>
            invoice.RecordPayment(Money.Of(49m, "EUR"), PaymentMethod.BankTransfer, null, null, Now));
    }

    [Fact]
    public void PayingInFullSettlesTheInvoice()
    {
        var invoice = Issued();

        invoice.RecordPayment(Money.Of(49m, "EUR"), PaymentMethod.BankTransfer, "REF-1", Guid.NewGuid(), Now.AddDays(1));

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(0m, invoice.OutstandingAmount);
        Assert.Equal(Now.AddDays(1), invoice.PaidAt);
    }

    [Fact]
    public void APartialPaymentLeavesTheRestOutstanding()
    {
        var invoice = Issued();

        invoice.RecordPayment(Money.Of(20m, "EUR"), PaymentMethod.BankTransfer, null, null, Now.AddDays(1));

        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Equal(29m, invoice.OutstandingAmount);
    }

    [Fact]
    public void TwoPartialPaymentsSettleIt()
    {
        var invoice = Issued();

        invoice.RecordPayment(Money.Of(20m, "EUR"), PaymentMethod.BankTransfer, null, null, Now.AddDays(1));
        invoice.RecordPayment(Money.Of(29m, "EUR"), PaymentMethod.BankTransfer, null, null, Now.AddDays(2));

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(2, invoice.Payments.Count);
    }

    [Fact]
    public void OverpaymentIsRecordedRatherThanRefused()
    {
        var invoice = Issued();

        // The money arrived; hiding that would make the ledger disagree with the bank.
        invoice.RecordPayment(Money.Of(60m, "EUR"), PaymentMethod.BankTransfer, null, null, Now.AddDays(1));

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(60m, invoice.PaidAmount);
        Assert.Equal(0m, invoice.OutstandingAmount);
    }

    [Fact]
    public void APaymentInAnotherCurrencyIsRefused()
    {
        var invoice = Issued();

        Assert.Throws<DomainException>(() =>
            invoice.RecordPayment(Money.Of(49m, "USD"), PaymentMethod.BankTransfer, null, null, Now));
    }

    [Fact]
    public void APaidInvoiceCannotBeVoided()
    {
        var invoice = Issued();
        invoice.RecordPayment(Money.Of(49m, "EUR"), PaymentMethod.BankTransfer, null, null, Now.AddDays(1));

        Assert.Throws<DomainException>(() => invoice.Void(Now.AddDays(2)));
    }

    [Fact]
    public void AVoidInvoiceCannotBePaid()
    {
        var invoice = Issued();
        invoice.Void(Now.AddDays(1));

        Assert.Throws<DomainException>(() =>
            invoice.RecordPayment(Money.Of(49m, "EUR"), PaymentMethod.BankTransfer, null, null, Now.AddDays(2)));
    }

    [Fact]
    public void OnlyAnIssuedInvoiceCanFallOverdue()
    {
        var draft = Draft();
        Assert.Throws<DomainException>(() => draft.MarkOverdue(Now));

        var invoice = Issued();
        invoice.MarkOverdue(Now.AddDays(15));
        Assert.Equal(InvoiceStatus.Overdue, invoice.Status);
    }

    [Fact]
    public void AnOverdueInvoiceCanStillBePaid()
    {
        var invoice = Issued();
        invoice.MarkOverdue(Now.AddDays(15));

        invoice.RecordPayment(Money.Of(49m, "EUR"), PaymentMethod.BankTransfer, null, null, Now.AddDays(16));

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }

    [Fact]
    public void AnInvoiceCannotFallDueBeforeItIsIssued()
    {
        var invoice = Draft();
        invoice.AddLine("Basic plan", null, 1, Money.Of(49m, "EUR"), Now);

        Assert.Throws<DomainException>(() => invoice.Issue("2026-000001", Now, Now));
    }

    [Fact]
    public void InvoiceNumbersAreFormattedForReading()
    {
        Assert.Equal("2026-000042", InvoiceNumberSequence.Format(2026, 42));
    }
}
