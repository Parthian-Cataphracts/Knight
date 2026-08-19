namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// The dashboard's landing figures, read straight from the control plane.
///
/// Only what exists today is reported. Server metrics, alerts and feature
/// delivery arrive in phases 3.5 to 5; until then their counters are zero and
/// their lists empty, which is the truth — not a placeholder pretending to be
/// data.
/// </summary>
public sealed record ControlPlaneOverview(
    CustomerCounts Customers,
    StoreCounts Stores,
    SubscriptionCounts Subscriptions,
    BillingCounts Billing,
    IReadOnlyCollection<ActivityEntry> RecentActivity);

public sealed record CustomerCounts(int Total, int Active, int Suspended, int Prospect, int Archived);

public sealed record StoreCounts(int Total, int Connected, int Degraded, int Disconnected, int NotRegistered);

public sealed record SubscriptionCounts(int Total, int Active, int Trial, int PastDue, int Suspended, int ActiveEntitlements);

public sealed record BillingCounts(int Draft, int Issued, int Overdue, int Paid, decimal OutstandingTotal, string? Currency);

public sealed record ActivityEntry(Guid Id, string Action, string TargetType, string? TargetId, string? Actor, DateTimeOffset OccurredAt);

public interface IControlPlaneOverviewReader
{
    Task<ControlPlaneOverview> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The two facts the customers list shows beside each row that do not live on
/// the customer itself: how many stores it has, and which plan it is on. Read as
/// one query for the whole page rather than one per row.
/// </summary>
public sealed record CustomerSummary(Guid CustomerId, int StoreCount, string? PlanKey);

public interface ICustomerDirectoryReader
{
    Task<IReadOnlyDictionary<Guid, CustomerSummary>> SummariseAsync(
        IReadOnlyCollection<Guid> customerIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// How many customers sit on each plan. Read once for the whole price list
/// rather than once per plan card.
/// </summary>
public interface IPlanSubscriberReader
{
    Task<IReadOnlyDictionary<Guid, int>> CountByPlanAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The commercial facts about a feature that the catalogue screen shows beside
/// each row: which plans offer it, and how many customers hold it.
/// </summary>
public sealed record FeatureUsage(Guid FeatureId, IReadOnlyCollection<string> PlanKeys, int EntitledCount);

public interface IFeatureUsageReader
{
    Task<IReadOnlyDictionary<Guid, FeatureUsage>> SummariseAsync(
        IReadOnlyCollection<Guid> featureIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Names for identifiers the dashboard shows in a list: customers, plans and
/// roles. Resolved for a whole page at once so a list never issues one query per
/// row.
/// </summary>
public interface ILabelReader
{
    Task<IReadOnlyDictionary<Guid, string>> CustomerNamesAsync(
        IReadOnlyCollection<Guid> customerIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, (string Key, string Name)>> PlanNamesAsync(
        IReadOnlyCollection<Guid> planIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Display names for accounts, so a record of who did something reads as a
    /// person rather than as an identifier. Looked up in one query for a whole
    /// timeline: an incident that ran for a day has many entries and very few
    /// distinct people.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>> RoleNamesForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, int>> RoleMemberCountsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Store names by id, for screens that show telemetry rows and need to say
    /// which store each came from without a query per row.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> StoreNamesAsync(
        IReadOnlyCollection<Guid> storeIds,
        CancellationToken cancellationToken);
}
