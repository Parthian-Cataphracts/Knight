using Billing;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Options;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Decides when to invoice.
///
/// Until phase 10 nothing did. `PrepareInvoiceAsync` knew how to build an
/// invoice for a subscription's period and every caller was a person clicking a
/// button, so a customer whose period closed while nobody was looking simply was
/// not billed. That is the gap TODO.md carried from phase 2 as "a billing run
/// that decides *when* to invoice and rolls the period forward — scheduled work,
/// deferred to phase 10 rather than hidden inside issuing".
///
/// It stays a separate, explicit operation rather than something folded into
/// issuing: the decision to bill somebody should be visible, auditable, and
/// testable without a clock.
///
/// By default it prepares drafts and does not issue them
/// (`Billing:IssueAutomatically`). Issuing consumes a gapless invoice number and
/// is the point after which an invoice cannot simply be corrected; this project
/// has never sent an invoice to anybody, and that is a decision the business
/// makes once rather than a default it inherits.
///
/// Every failure is caught and logged. A background service that throws is one
/// that has stopped running, and a billing run that quietly stopped is a month
/// of revenue nobody notices.
/// </summary>
public sealed class BillingRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BillingRunner> _logger;
    private readonly BillingOptions _options;

    public BillingRunner(
        IServiceScopeFactory scopes,
        ILogger<BillingRunner> logger,
        IOptions<BillingOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Billing run started; checking every {Interval} for closed periods, issuing automatically: {Issue}.",
            _options.RunInterval,
            _options.IssueAutomatically);

        using var timer = new PeriodicTimer(_options.RunInterval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                using var pass = BackgroundCorrelation.BeginPass("billing run");
                using var scope = _scopes.CreateScope();

                // Billing spans every customer, so this is platform work. Without
                // an explicit scope the isolation filter fails closed and the run
                // finds nothing to bill — which looks exactly like everything
                // being up to date.
                scope.ServiceProvider
                    .GetRequiredService<ICustomerScopeAccessor>()
                    .SetPlatformScope();

                var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
                var result = await billing.RunAsync(stoppingToken);

                if (result.Considered > 0)
                {
                    _logger.LogInformation(
                        "Billing run: {Considered} subscription(s) due, {Invoiced} invoiced, {Issued} issued, {Failed} failed.",
                        result.Considered,
                        result.Invoiced,
                        result.Issued,
                        result.Failed);
                }

                if (result.Failed > 0)
                {
                    _logger.LogWarning("{Failed} subscription(s) could not be billed; see the audit trail.", result.Failed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The billing run failed; it will run again next interval.");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
