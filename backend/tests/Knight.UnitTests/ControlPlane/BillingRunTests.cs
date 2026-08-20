using Billing;
using Billing.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The billing run — the thing that decides *when* an invoice gets made, which
/// until phase 10 was nobody's job.
///
/// The properties worth holding are about money and about not losing a period:
/// only active subscriptions are billed, a period is never billed and then
/// silently skipped, and one customer's bad data does not stop everybody else
/// being invoiced.
/// </summary>
public sealed class BillingRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly ISubscriptionReader _subscriptions = Substitute.For<ISubscriptionReader>();
    private readonly ISubscriptionPeriodWriter _periods = Substitute.For<ISubscriptionPeriodWriter>();
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly IBillingAccountRepository _accounts = Substitute.For<IBillingAccountRepository>();
    private readonly IPricingReader _pricing = Substitute.For<IPricingReader>();
    private readonly IAuditTrail _audit = Substitute.For<IAuditTrail>();
    private readonly IAuditContext _actor = Substitute.For<IAuditContext>();

    private static SubscriptionSnapshot Due(Guid? id = null, string status = "Active") =>
        new(id ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), status, [],
            Now.AddDays(-30), Now.AddDays(-1));

    private BillingService Service(BillingOptions? options = null)
    {
        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(Now);

        _pricing.QuoteAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new QuotedPrice("IRR", 0m, []));

        return new BillingService(
            _invoices,
            _accounts,
            _subscriptions,
            _periods,
            _pricing,
            _audit,
            _actor,
            clock,
            Options.Create(options ?? new BillingOptions()));
    }

    [Fact]
    public async Task ARunWithNothingDueDoesNothingAndSaysSo()
    {
        _subscriptions.ListDueForBillingAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await Service().RunAsync(CancellationToken.None);

        Assert.Equal(new BillingRunResult(0, 0, 0, 0), result);
        await _periods.DidNotReceive().AdvancePeriodAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EachDueSubscriptionIsInvoicedAndRolledForward()
    {
        var subscription = Due();
        _subscriptions.ListDueForBillingAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([subscription]);
        _subscriptions.GetAsync(subscription.SubscriptionId, Arg.Any<CancellationToken>()).Returns(subscription);

        var options = new BillingOptions { BillingPeriod = TimeSpan.FromDays(30) };
        var result = await Service(options).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Considered);
        Assert.Equal(1, result.Invoiced);

        // Drafts by default: issuing consumes a gapless number and is not
        // something a default should start doing on its own.
        Assert.Equal(0, result.Issued);

        await _periods.Received(1).AdvancePeriodAsync(
            subscription.SubscriptionId,
            subscription.CurrentPeriodEnd.AddDays(30),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheNewPeriodStartsWhereTheOldOneEndedNotAtTheRunTime()
    {
        // A run that is late — the machine was down for two days — must not
        // create a gap in the billing periods. The next period starts where the
        // last one ended, whenever the run happens to notice.
        var subscription = new SubscriptionSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Active", [],
            Now.AddDays(-40), Now.AddDays(-10));

        _subscriptions.ListDueForBillingAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([subscription]);
        _subscriptions.GetAsync(subscription.SubscriptionId, Arg.Any<CancellationToken>()).Returns(subscription);

        await Service(new BillingOptions { BillingPeriod = TimeSpan.FromDays(30) }).RunAsync(CancellationToken.None);

        await _periods.Received(1).AdvancePeriodAsync(
            subscription.SubscriptionId,
            Now.AddDays(-10).AddDays(30),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneBadSubscriptionDoesNotStopTheRest()
    {
        var broken = Due();
        var healthy = Due();

        _subscriptions.ListDueForBillingAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([broken, healthy]);

        _subscriptions.GetAsync(broken.SubscriptionId, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("its plan was deleted"));
        _subscriptions.GetAsync(healthy.SubscriptionId, Arg.Any<CancellationToken>()).Returns(healthy);

        var result = await Service().RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Considered);
        Assert.Equal(1, result.Invoiced);
        Assert.Equal(1, result.Failed);

        // The healthy one still rolled forward; the broken one did not, so it
        // will be retried next run rather than skipped for good.
        await _periods.Received(1).AdvancePeriodAsync(healthy.SubscriptionId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _periods.DidNotReceive().AdvancePeriodAsync(broken.SubscriptionId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailureIsAuditedAgainstTheSubscriptionThatFailed()
    {
        var broken = Due();

        _subscriptions.ListDueForBillingAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([broken]);
        _subscriptions.GetAsync(broken.SubscriptionId, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("its plan was deleted"));

        await Service().RunAsync(CancellationToken.None);

        // "Why was this customer not invoiced last month" is asked weeks later,
        // by which time a log line has rotated away.
        await _audit.Received(1).RecordAsync(
            "billing.run_failed",
            "Subscription",
            broken.SubscriptionId.ToString(),
            broken.CustomerId,
            Arg.Any<CancellationToken>(),
            Arg.Any<object?>(),
            Arg.Any<object?>());
    }

    [Fact]
    public async Task TheBatchSizeCapsOnePass()
    {
        // A backlog must not become one enormous transaction.
        var due = Enumerable.Range(0, 10).Select(_ => Due()).ToArray();

        _subscriptions.ListDueForBillingAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(due);

        foreach (var subscription in due)
        {
            _subscriptions.GetAsync(subscription.SubscriptionId, Arg.Any<CancellationToken>()).Returns(subscription);
        }

        var result = await Service(new BillingOptions { RunBatchSize = 3 }).RunAsync(CancellationToken.None);

        Assert.Equal(3, result.Considered);
    }
}
