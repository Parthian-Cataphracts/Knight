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
/// The facts about a feature that the catalogue screen shows beside each row.
///
/// Two of them are commercial — which plans offer it, how many customers hold
/// it — and two are about delivery: the newest version an operator could install
/// and how many stores are running it today. Entitlement and installation are
/// separate facts (docs/README.md), so a feature can perfectly well be sold to
/// twenty customers and installed nowhere.
/// </summary>
public sealed record FeatureUsage(
    Guid FeatureId,
    IReadOnlyCollection<string> PlanKeys,
    int EntitledCount,
    string? LatestVersion,
    int InstallCount);

public interface IFeatureUsageReader
{
    Task<IReadOnlyDictionary<Guid, FeatureUsage>> SummariseAsync(
        IReadOnlyCollection<Guid> featureIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// How many features are actually installed on each store, for the store list
/// and the fleet health screen.
///
/// A separate read rather than a column on the store: installation is owned by
/// the delivery subsystem and is a different fact from entitlement, so a store
/// entitled to six features and running two is a perfectly ordinary state that
/// the store aggregate has no business knowing about.
/// </summary>
public interface IStoreFeatureCountReader
{
    Task<IReadOnlyDictionary<Guid, int>> CountInstalledAsync(
        IReadOnlyCollection<Guid> storeIds,
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

    /// <summary>Feature names by id, for screens that hold a feature id and must show a name.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FeatureNamesAsync(
        IReadOnlyCollection<Guid> featureIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Which features each customer is currently entitled to.
    ///
    /// Read for a whole page at once, because the installations screen compares
    /// entitlement against installation on every row and doing it per row would
    /// turn one screen into a query storm.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>>> EntitledFeaturesAsync(
        IReadOnlyCollection<Guid> customerIds,
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
