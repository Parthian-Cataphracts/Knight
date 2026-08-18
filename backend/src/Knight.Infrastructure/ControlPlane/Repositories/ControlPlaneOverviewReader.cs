using AccessControl.Domain;
using Billing.Domain;
using Customers.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Stores.Domain;
using Subscriptions.Domain;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Aggregates the dashboard's landing figures.
///
/// Every query runs through the same customer-isolation filter as everything
/// else, so a customer-scoped principal sees counts of their own world rather
/// than the platform's.
/// </summary>
internal sealed class ControlPlaneOverviewReader : IControlPlaneOverviewReader
{
    private readonly ControlPlaneDbContext _context;

    public ControlPlaneOverviewReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<ControlPlaneOverview> ReadAsync(CancellationToken cancellationToken)
    {
        var customers = await _context.Customers
            .GroupBy(customer => customer.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);

        var stores = await _context.Stores
            .GroupBy(store => store.IntegrationStatus)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);

        var subscriptions = await _context.Subscriptions
            .GroupBy(subscription => subscription.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);

        var entitlements = await _context.FeatureEntitlements
            .CountAsync(entitlement => entitlement.RevokedAt == null, cancellationToken);

        var invoices = await _context.Invoices
            .GroupBy(invoice => invoice.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);

        var outstanding = await _context.Invoices
            .Where(invoice => invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.Overdue)
            .Select(invoice => new { invoice.Total, invoice.Currency })
            .ToArrayAsync(cancellationToken);

        var activity = await _context.AuditLogs
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(10)
            .Select(entry => new ActivityEntry(
                entry.Id,
                entry.Action,
                entry.TargetType,
                entry.TargetId,
                entry.ActorDisplay,
                entry.OccurredAt))
            .ToArrayAsync(cancellationToken);

        int CustomerCount(CustomerStatus status) =>
            customers.FirstOrDefault(row => row.Status == status)?.Count ?? 0;

        int StoreCount(IntegrationStatus status) =>
            stores.FirstOrDefault(row => row.Status == status)?.Count ?? 0;

        int SubscriptionCount(SubscriptionStatus status) =>
            subscriptions.FirstOrDefault(row => row.Status == status)?.Count ?? 0;

        int InvoiceCount(InvoiceStatus status) =>
            invoices.FirstOrDefault(row => row.Status == status)?.Count ?? 0;

        return new ControlPlaneOverview(
            new CustomerCounts(
                customers.Sum(row => row.Count),
                CustomerCount(CustomerStatus.Active),
                CustomerCount(CustomerStatus.Suspended),
                CustomerCount(CustomerStatus.Prospect),
                CustomerCount(CustomerStatus.Archived)),
            new StoreCounts(
                stores.Sum(row => row.Count),
                StoreCount(IntegrationStatus.Connected),
                StoreCount(IntegrationStatus.Degraded),
                StoreCount(IntegrationStatus.Disconnected),
                StoreCount(IntegrationStatus.NotRegistered)),
            new SubscriptionCounts(
                subscriptions.Sum(row => row.Count),
                SubscriptionCount(SubscriptionStatus.Active),
                SubscriptionCount(SubscriptionStatus.Trial),
                SubscriptionCount(SubscriptionStatus.PastDue),
                SubscriptionCount(SubscriptionStatus.Suspended),
                entitlements),
            new BillingCounts(
                InvoiceCount(InvoiceStatus.Draft),
                InvoiceCount(InvoiceStatus.Issued),
                InvoiceCount(InvoiceStatus.Overdue),
                InvoiceCount(InvoiceStatus.Paid),

                // Summed in the application rather than the database: mixing
                // currencies in one SUM would produce a number that means
                // nothing, so the currency is reported alongside and is null
                // when there is nothing outstanding or more than one currency.
                outstanding.Sum(invoice => invoice.Total),
                outstanding.Select(invoice => invoice.Currency).Distinct().Count() == 1
                    ? outstanding[0].Currency
                    : null),
            activity);
    }
}

/// <summary>
/// Summarises a page of customers in two queries rather than two per row.
/// </summary>
internal sealed class CustomerDirectoryReader : ICustomerDirectoryReader
{
    private readonly ControlPlaneDbContext _context;

    public CustomerDirectoryReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<Guid, CustomerSummary>> SummariseAsync(
        IReadOnlyCollection<Guid> customerIds,
        CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return new Dictionary<Guid, CustomerSummary>();
        }

        var storeCounts = await _context.Stores
            .Where(store => customerIds.Contains(store.CustomerId))
            .GroupBy(store => store.CustomerId)
            .Select(group => new { CustomerId = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);

        // A cancelled subscription is not the customer's plan any more, so it is
        // excluded rather than shown as if it still applied.
        var plans = await _context.Subscriptions
            .Where(subscription =>
                customerIds.Contains(subscription.CustomerId) &&
                subscription.Status != SubscriptionStatus.Cancelled)
            .Join(
                _context.Plans,
                subscription => subscription.PlanId,
                plan => plan.Id,
                (subscription, plan) => new { subscription.CustomerId, plan.Key })
            .ToArrayAsync(cancellationToken);

        return customerIds.ToDictionary(
            id => id,
            id => new CustomerSummary(
                id,
                storeCounts.FirstOrDefault(row => row.CustomerId == id)?.Count ?? 0,
                plans.FirstOrDefault(row => row.CustomerId == id)?.Key));
    }
}

internal sealed class PlanSubscriberReader : IPlanSubscriberReader
{
    private readonly ControlPlaneDbContext _context;

    public PlanSubscriberReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountByPlanAsync(CancellationToken cancellationToken)
    {
        // A cancelled subscription is not a customer on the plan any more.
        var counts = await _context.Subscriptions
            .Where(subscription => subscription.Status != SubscriptionStatus.Cancelled)
            .GroupBy(subscription => subscription.PlanId)
            .Select(group => new { PlanId = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);

        return counts.ToDictionary(row => row.PlanId, row => row.Count);
    }
}

internal sealed class FeatureUsageReader : IFeatureUsageReader
{
    private readonly ControlPlaneDbContext _context;

    public FeatureUsageReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<Guid, FeatureUsage>> SummariseAsync(
        IReadOnlyCollection<Guid> featureIds,
        CancellationToken cancellationToken)
    {
        if (featureIds.Count == 0)
        {
            return new Dictionary<Guid, FeatureUsage>();
        }

        var offerings = await _context.Set<Plans.Domain.PlanFeature>()
            .Where(entry => featureIds.Contains(entry.FeatureId))
            .Join(
                _context.Plans,
                entry => entry.PlanId,
                plan => plan.Id,
                (entry, plan) => new { entry.FeatureId, plan.Key })
            .ToArrayAsync(cancellationToken);

        var entitled = await _context.FeatureEntitlements
            .Where(entitlement => featureIds.Contains(entitlement.FeatureId) && entitlement.RevokedAt == null)
            .GroupBy(entitlement => entitlement.FeatureId)
            .Select(group => new { FeatureId = group.Key, Count = group.Select(e => e.CustomerId).Distinct().Count() })
            .ToArrayAsync(cancellationToken);

        return featureIds.ToDictionary(
            id => id,
            id => new FeatureUsage(
                id,
                offerings.Where(row => row.FeatureId == id).Select(row => row.Key).Distinct().ToArray(),
                entitled.FirstOrDefault(row => row.FeatureId == id)?.Count ?? 0));
    }
}
