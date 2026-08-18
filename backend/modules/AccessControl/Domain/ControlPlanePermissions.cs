using Knight.Domain.Exceptions;

namespace AccessControl.Domain;

/// <summary>
/// The complete set of control-plane permission keys, exactly as listed in
/// docs/authorization.md section 2. A key that is not here cannot be granted:
/// a typo in a role definition would otherwise create a permission nobody holds
/// and an endpoint nobody can reach, which is a silent lockout rather than a
/// visible error.
///
/// The feature-lifecycle keys are split by blast radius on purpose. Editing
/// registry metadata is routine; publishing ships executable code to every
/// entitled store, and uninstalling eventually removes data — those are separate
/// grants even though the same person often holds all three.
/// </summary>
public static class ControlPlanePermissions
{
    public const string CustomerView = "customer.view";
    public const string CustomerCreate = "customer.create";
    public const string CustomerUpdate = "customer.update";
    public const string CustomerArchive = "customer.archive";

    public const string StoreView = "store.view";
    public const string StoreCreate = "store.create";
    public const string StoreManage = "store.manage";
    public const string StoreCredentialsManage = "store.credentials.manage";

    public const string PlanView = "plan.view";
    public const string PlanManage = "plan.manage";

    public const string FeatureView = "feature.view";
    public const string FeatureManage = "feature.manage";
    public const string FeaturePublish = "feature.publish";
    public const string FeatureYank = "feature.yank";

    public const string InstallationView = "installation.view";
    public const string InstallationManage = "installation.manage";
    public const string InstallationUninstall = "installation.uninstall";
    public const string InstallationRollback = "installation.rollback";

    public const string JobView = "job.view";
    public const string JobManage = "job.manage";

    public const string SubscriptionView = "subscription.view";
    public const string SubscriptionManage = "subscription.manage";

    public const string BillingView = "billing.view";
    public const string BillingManage = "billing.manage";

    public const string ServerView = "server.view";
    public const string ServerManage = "server.manage";
    public const string AgentManage = "agent.manage";

    public const string MonitoringView = "monitoring.view";
    public const string LogsView = "logs.view";
    public const string LogsExport = "logs.export";

    public const string ErrorsView = "errors.view";
    public const string ErrorsManage = "errors.manage";
    public const string IncidentView = "incident.view";
    public const string IncidentManage = "incident.manage";

    public const string NotificationManage = "notification.manage";

    public const string AuditView = "audit.view";
    public const string ReportView = "report.view";

    public const string UserView = "user.view";
    public const string UserManage = "user.manage";
    public const string RoleView = "role.view";
    public const string RoleManage = "role.manage";

    /// <summary>Narrow internal permission held by store principals only.</summary>
    public const string IngestWrite = "ingest.write";

    /// <summary>Narrow internal permissions held by agent principals only.</summary>
    public const string AgentReport = "agent.report";

    public const string AgentExecuteJob = "agent.executeJob";

    private static readonly string[] AllKeys =
    [
        CustomerView, CustomerCreate, CustomerUpdate, CustomerArchive,
        StoreView, StoreCreate, StoreManage, StoreCredentialsManage,
        PlanView, PlanManage,
        FeatureView, FeatureManage, FeaturePublish, FeatureYank,
        InstallationView, InstallationManage, InstallationUninstall, InstallationRollback,
        JobView, JobManage,
        SubscriptionView, SubscriptionManage,
        BillingView, BillingManage,
        ServerView, ServerManage, AgentManage,
        MonitoringView, LogsView, LogsExport,
        ErrorsView, ErrorsManage, IncidentView, IncidentManage,
        NotificationManage,
        AuditView, ReportView,
        UserView, UserManage, RoleView, RoleManage,
        IngestWrite, AgentReport, AgentExecuteJob,
    ];

    /// <summary>
    /// Permissions a customer-scoped role may hold. Everything else is platform
    /// business: defining plans, publishing executable code, removing code and
    /// data from a store, or managing the infrastructure it runs on. A customer
    /// holding any of those would be operating the platform, not their own
    /// account (docs/authorization.md section 2).
    /// </summary>
    private static readonly HashSet<string> CustomerAssignableKeys =
    [
        CustomerView, CustomerUpdate,
        StoreView, StoreManage, StoreCredentialsManage,
        PlanView,
        FeatureView,
        InstallationView, InstallationManage,
        JobView,
        SubscriptionView, SubscriptionManage,
        BillingView,
        ServerView,
        MonitoringView, LogsView,
        ErrorsView, ErrorsManage, IncidentView, IncidentManage,
        NotificationManage,
        AuditView, ReportView,
        UserView, UserManage, RoleView,
    ];

    /// <summary>Permissions minted for machine principals, never assignable to a human role.</summary>
    private static readonly HashSet<string> MachineOnlyKeys =
    [
        IngestWrite, AgentReport, AgentExecuteJob,
    ];

    public static IReadOnlyCollection<string> All => AllKeys;

    public static IReadOnlyCollection<string> AssignableToRoles =>
        AllKeys.Where(key => !MachineOnlyKeys.Contains(key)).ToArray();

    public static bool Exists(string? key) => key is not null && AllKeys.Contains(key);

    public static bool IsCustomerAssignable(string key) => CustomerAssignableKeys.Contains(key);

    /// <summary>Returns the canonical key or refuses it; never invents one.</summary>
    public static string Require(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw DomainException.Validation("A permission key is required.");
        }

        var trimmed = key.Trim();
        if (!AllKeys.Contains(trimmed))
        {
            throw DomainException.Validation($"'{trimmed}' is not a known permission.");
        }

        if (MachineOnlyKeys.Contains(trimmed))
        {
            throw DomainException.Conflict($"Permission '{trimmed}' belongs to a machine principal and cannot be granted to a role.");
        }

        return trimmed;
    }
}
