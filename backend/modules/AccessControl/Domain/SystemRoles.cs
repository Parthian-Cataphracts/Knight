namespace AccessControl.Domain;

/// <summary>
/// The seeded default roles from docs/authorization.md section 1. Their
/// definitions live in code because a fresh deployment must come up with a
/// usable access model; the rows themselves are ordinary data, and operators may
/// add roles of their own alongside them.
/// </summary>
public static class SystemRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Developer = "Developer";
    public const string Support = "Support";
    public const string CustomerOwner = "CustomerOwner";
    public const string CustomerStaff = "CustomerStaff";

    /// <summary>Platform roles that must present a second factor before they may act.</summary>
    public static readonly string[] MfaRequired = [SuperAdmin, Admin];

    public sealed record Definition(string Name, RoleScope Scope, string Description, IReadOnlyCollection<string> Permissions);

    public static IReadOnlyCollection<Definition> All =>
    [
        new(
            SuperAdmin,
            RoleScope.Platform,
            "Everything, including role and plan definition.",
            ControlPlanePermissions.AssignableToRoles),

        new(
            Admin,
            RoleScope.Platform,
            "Day-to-day operation of all customers.",
            [
                ControlPlanePermissions.CustomerView, ControlPlanePermissions.CustomerCreate,
                ControlPlanePermissions.CustomerUpdate, ControlPlanePermissions.CustomerArchive,
                ControlPlanePermissions.StoreView, ControlPlanePermissions.StoreCreate,
                ControlPlanePermissions.StoreManage, ControlPlanePermissions.StoreCredentialsManage,
                ControlPlanePermissions.PlanView,
                ControlPlanePermissions.FeatureView, ControlPlanePermissions.FeatureManage,
                ControlPlanePermissions.InstallationView, ControlPlanePermissions.InstallationManage,
                ControlPlanePermissions.InstallationUninstall, ControlPlanePermissions.InstallationRollback,
                ControlPlanePermissions.JobView, ControlPlanePermissions.JobManage,
                ControlPlanePermissions.SubscriptionView, ControlPlanePermissions.SubscriptionManage,
                ControlPlanePermissions.BillingView,
                ControlPlanePermissions.ServerView, ControlPlanePermissions.ServerManage,
                ControlPlanePermissions.AgentManage,
                ControlPlanePermissions.MonitoringView, ControlPlanePermissions.LogsView,
                ControlPlanePermissions.ErrorsView, ControlPlanePermissions.ErrorsManage,
                ControlPlanePermissions.IncidentView, ControlPlanePermissions.IncidentManage,
                ControlPlanePermissions.NotificationManage,
                ControlPlanePermissions.AuditView, ControlPlanePermissions.ReportView,
                ControlPlanePermissions.UserView, ControlPlanePermissions.UserManage,
                ControlPlanePermissions.RoleView,
            ]),

        new(
            Developer,
            RoleScope.Platform,
            "Monitoring, errors, logs, incidents and deployments; no billing.",
            [
                ControlPlanePermissions.CustomerView,
                ControlPlanePermissions.StoreView,
                ControlPlanePermissions.FeatureView,
                ControlPlanePermissions.InstallationView, ControlPlanePermissions.InstallationManage,
                ControlPlanePermissions.JobView, ControlPlanePermissions.JobManage,
                ControlPlanePermissions.ServerView,
                ControlPlanePermissions.MonitoringView, ControlPlanePermissions.LogsView,
                ControlPlanePermissions.ErrorsView, ControlPlanePermissions.ErrorsManage,
                ControlPlanePermissions.IncidentView, ControlPlanePermissions.IncidentManage,
                ControlPlanePermissions.ReportView,
            ]),

        new(
            Support,
            RoleScope.Platform,
            "Read-mostly across customers; incident notes.",
            [
                ControlPlanePermissions.CustomerView,
                ControlPlanePermissions.StoreView,
                ControlPlanePermissions.FeatureView,
                ControlPlanePermissions.InstallationView,
                ControlPlanePermissions.JobView,
                ControlPlanePermissions.SubscriptionView,
                ControlPlanePermissions.ServerView,
                ControlPlanePermissions.MonitoringView, ControlPlanePermissions.LogsView,
                ControlPlanePermissions.ErrorsView,
                ControlPlanePermissions.IncidentView, ControlPlanePermissions.IncidentManage,
                ControlPlanePermissions.ReportView,
            ]),

        new(
            CustomerOwner,
            RoleScope.Customer,
            "Full access to their own customer, including the subscription.",
            [
                ControlPlanePermissions.CustomerView, ControlPlanePermissions.CustomerUpdate,
                ControlPlanePermissions.StoreView, ControlPlanePermissions.StoreManage,
                ControlPlanePermissions.StoreCredentialsManage,
                ControlPlanePermissions.PlanView,
                ControlPlanePermissions.FeatureView,
                ControlPlanePermissions.InstallationView, ControlPlanePermissions.InstallationManage,
                ControlPlanePermissions.JobView,
                ControlPlanePermissions.SubscriptionView, ControlPlanePermissions.SubscriptionManage,
                ControlPlanePermissions.BillingView,
                ControlPlanePermissions.MonitoringView, ControlPlanePermissions.LogsView,
                ControlPlanePermissions.ErrorsView, ControlPlanePermissions.ErrorsManage,
                ControlPlanePermissions.IncidentView, ControlPlanePermissions.IncidentManage,
                ControlPlanePermissions.NotificationManage,
                ControlPlanePermissions.AuditView, ControlPlanePermissions.ReportView,
                ControlPlanePermissions.UserView, ControlPlanePermissions.UserManage,
                ControlPlanePermissions.RoleView,
            ]),

        new(
            CustomerStaff,
            RoleScope.Customer,
            "Read-mostly access to their own customer's stores and errors.",
            [
                ControlPlanePermissions.CustomerView,
                ControlPlanePermissions.StoreView,
                ControlPlanePermissions.FeatureView,
                ControlPlanePermissions.InstallationView,
                ControlPlanePermissions.JobView,
                ControlPlanePermissions.MonitoringView, ControlPlanePermissions.LogsView,
                ControlPlanePermissions.ErrorsView,
                ControlPlanePermissions.IncidentView,
                ControlPlanePermissions.ReportView,
            ]),
    ];

    public static bool RequiresMfa(IEnumerable<string> roleNames) =>
        roleNames.Any(name => MfaRequired.Contains(name, StringComparer.Ordinal));
}
