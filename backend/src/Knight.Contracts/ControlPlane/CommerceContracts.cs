namespace Knight.Contracts.ControlPlane;

// --- Feature catalogue ---------------------------------------------------

public sealed record CreateFeatureRequest
{
    public required string Slug { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Category { get; init; }

    public bool IsOptional { get; init; } = true;

    /// <summary>True when the capability cannot run on shared hosting.</summary>
    public bool RequiresDedicatedInfrastructure { get; init; }
}

public sealed record UpdateFeatureRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Category { get; init; }
}

public sealed record FeatureResponse
{
    public required Guid Id { get; init; }

    public required string Slug { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Category { get; init; }

    public required bool IsOptional { get; init; }

    public required bool RequiresDedicatedInfrastructure { get; init; }

    /// <summary>
    /// The larger Feature this one is a part of, or null for a top-level Feature.
    /// A composed Feature groups its parts under one parent so the portal can
    /// present them on one page and sum their prices
    /// (docs/adr/0037-composed-pricing-and-sub-features.md).
    /// </summary>
    public Guid? ParentFeatureId { get; init; }

    /// <summary>Draft, Published, Deprecated or Withdrawn.</summary>
    public required string Status { get; init; }

    /// <summary>Keys of the plans that offer this feature, included or optional.</summary>
    public required IReadOnlyCollection<string> Plans { get; init; }

    /// <summary>How many customers currently hold an active entitlement for it.</summary>
    public required int EntitledCount { get; init; }

    /// <summary>
    /// Null until the registry exists: a feature identity is sellable long
    /// before any version of it has been published (phase 3.5).
    /// </summary>
    public string? LatestVersion { get; init; }

    /// <summary>
    /// Null until delivery exists. Zero would read as "installed nowhere",
    /// which is a different claim from "not knowable yet".
    /// </summary>
    public int? InstallCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

// --- Plans ---------------------------------------------------------------

public sealed record CreatePlanRequest
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required decimal BasePrice { get; init; }

    public required string Currency { get; init; }

    public int SortOrder { get; init; }
}

public sealed record UpdatePlanRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required decimal BasePrice { get; init; }

    public required string Currency { get; init; }

    public int SortOrder { get; init; }

    /// <summary>Days a customer on this plan keeps their data after deprovisioning; null falls back to the deployment default.</summary>
    public int? DataRetentionDays { get; init; }
}

public sealed record SetPlanFeatureRequest
{
    public required Guid FeatureId { get; init; }

    /// <summary>True when the plan grants the feature without the customer choosing it.</summary>
    public required bool IsIncluded { get; init; }

    /// <summary>False means only platform staff may change it, by changing the plan.</summary>
    public required bool IsCustomerToggleable { get; init; }

    /// <summary>Optional semver range; null means the latest compatible published version.</summary>
    public string? PinnedVersionRange { get; init; }
}

public sealed record PlanFeatureResponse
{
    public required Guid FeatureId { get; init; }

    /// <summary>The feature's slug and name, so a plan can be read without a second call per row.</summary>
    public required string FeatureSlug { get; init; }

    public required string FeatureName { get; init; }

    public required bool IsIncluded { get; init; }

    public required bool IsCustomerToggleable { get; init; }

    public string? PinnedVersionRange { get; init; }
}

public sealed record PlanResponse
{
    public required Guid Id { get; init; }

    public required string Key { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required decimal BasePrice { get; init; }

    public required string Currency { get; init; }

    public required bool IsActive { get; init; }

    public required int SortOrder { get; init; }

    /// <summary>How long a customer on this plan keeps their data after deprovisioning. Null means the deployment default.</summary>
    public int? DataRetentionDays { get; init; }

    public required IReadOnlyCollection<PlanFeatureResponse> Features { get; init; }

    /// <summary>Slugs the plan grants outright, derived from <see cref="Features"/> so every client groups them the same way.</summary>
    public required IReadOnlyCollection<string> IncludedFeatures { get; init; }

    /// <summary>Slugs the customer may switch on for themselves.</summary>
    public required IReadOnlyCollection<string> OptionalFeatures { get; init; }

    /// <summary>How many customers are on this plan right now, cancelled subscriptions excluded.</summary>
    public required int CustomerCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record SetFeaturePriceRequest
{
    public required Guid FeatureId { get; init; }

    /// <summary>Null prices the feature on every plan that does not override it.</summary>
    public Guid? PlanId { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>Monthly, Yearly or OneTime.</summary>
    public required string BillingPeriod { get; init; }
}

public sealed record FeaturePriceResponse
{
    public required Guid Id { get; init; }

    public required Guid FeatureId { get; init; }

    public Guid? PlanId { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string BillingPeriod { get; init; }

    public required DateTimeOffset ValidFrom { get; init; }

    public DateTimeOffset? ValidTo { get; init; }
}

// --- Subscriptions -------------------------------------------------------

public sealed record CreateSubscriptionRequest
{
    public required Guid CustomerId { get; init; }

    public required Guid PlanId { get; init; }

    public IReadOnlyCollection<Guid> FeatureIds { get; init; } = [];

    public bool AsTrial { get; init; }
}

public sealed record ChangePlanRequest
{
    public required Guid PlanId { get; init; }
}

public sealed record SetSubscriptionFeaturesRequest
{
    public required IReadOnlyCollection<Guid> FeatureIds { get; init; }
}

public sealed record SubscriptionFeatureResponse
{
    public required Guid FeatureId { get; init; }

    public required bool IsEnabled { get; init; }

    public required DateTimeOffset EnabledAt { get; init; }
}

public sealed record SubscriptionResponse
{
    public required Guid Id { get; init; }

    public required Guid CustomerId { get; init; }

    public required string CustomerName { get; init; }

    public required Guid PlanId { get; init; }

    public required string PlanKey { get; init; }

    public required string PlanName { get; init; }

    /// <summary>Trial, Active, PastDue, Suspended or Cancelled.</summary>
    public required string Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CurrentPeriodStart { get; init; }

    public required DateTimeOffset CurrentPeriodEnd { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public required IReadOnlyCollection<SubscriptionFeatureResponse> Features { get; init; }

    /// <summary>How many optional features the customer has switched on.</summary>
    public required int OptionalFeatures { get; init; }

    /// <summary>
    /// What this subscription costs per month at today's prices: the plan plus
    /// the optional features switched on.
    ///
    /// Priced through the same calculator that produces a quote and an invoice,
    /// so a customer cannot be shown one figure here and billed another. It is
    /// recomputed on read rather than stored, because a price list change must
    /// not silently rewrite what a past invoice said.
    /// </summary>
    public required decimal MonthlyTotal { get; init; }

    /// <summary>ISO currency code the total is expressed in.</summary>
    public required string Currency { get; init; }
}

public sealed record QuoteRequestBody
{
    public required Guid PlanId { get; init; }

    public IReadOnlyCollection<Guid> FeatureIds { get; init; } = [];
}

public sealed record QuoteLineResponse
{
    public required string Description { get; init; }

    public Guid? FeatureId { get; init; }

    public required int Quantity { get; init; }

    public required decimal UnitPrice { get; init; }

    public required decimal Total { get; init; }
}

public sealed record QuoteResponse
{
    public required string Currency { get; init; }

    public required decimal Subtotal { get; init; }

    public required IReadOnlyCollection<QuoteLineResponse> Lines { get; init; }
}

// --- Entitlements --------------------------------------------------------

public sealed record EntitlementResponse
{
    public required Guid FeatureId { get; init; }

    /// <summary>Plan, Optional or Grant.</summary>
    public required string Source { get; init; }

    public required DateTimeOffset GrantedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public required bool IsActive { get; init; }
}

public sealed record GrantEntitlementRequest
{
    public required Guid FeatureId { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record RevokeEntitlementRequest
{
    public required string Reason { get; init; }
}

/// <summary>
/// Whether a feature could be entitled to a customer, and if not, why. The
/// dashboard asks this before offering a button, so the refusal is data rather
/// than an error.
/// </summary>
public sealed record EntitlementCheckResponse
{
    public required bool IsAllowed { get; init; }

    /// <summary>None, FeatureNotAvailable, NotOfferedByPlan, NoEntitlingSubscription or RequiresDedicatedInfrastructure.</summary>
    public required string Refusal { get; init; }

    public string? Detail { get; init; }
}

// --- Billing -------------------------------------------------------------

public sealed record OpenBillingAccountRequest
{
    public required Guid CustomerId { get; init; }

    public required string Currency { get; init; }

    public required string BillingEmail { get; init; }

    public string? TaxId { get; init; }
}

public sealed record BillingAccountResponse
{
    public required Guid Id { get; init; }

    public required Guid CustomerId { get; init; }

    public required string Currency { get; init; }

    public required string BillingEmail { get; init; }

    public string? TaxId { get; init; }
}

public sealed record RecordPaymentRequest
{
    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    /// <summary>BankTransfer, Card, Cash, Credit or Other.</summary>
    public required string Method { get; init; }

    public string? Reference { get; init; }

    public DateTimeOffset? PaidAt { get; init; }
}

public sealed record InvoiceLineResponse
{
    public required Guid Id { get; init; }

    public required string Description { get; init; }

    public Guid? FeatureId { get; init; }

    public required int Quantity { get; init; }

    public required decimal UnitPrice { get; init; }

    public required decimal Total { get; init; }
}

public sealed record PaymentResponse
{
    public required Guid Id { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string Method { get; init; }

    public string? Reference { get; init; }

    public required DateTimeOffset PaidAt { get; init; }
}

public sealed record InvoiceResponse
{
    public required Guid Id { get; init; }

    public required Guid CustomerId { get; init; }

    public required string CustomerName { get; init; }

    public Guid? SubscriptionId { get; init; }

    /// <summary>Assigned at issue time; null while the invoice is a draft.</summary>
    public string? Number { get; init; }

    public required DateTimeOffset PeriodStart { get; init; }

    public required DateTimeOffset PeriodEnd { get; init; }

    public required decimal Subtotal { get; init; }

    public required decimal Tax { get; init; }

    public required decimal Total { get; init; }

    public required decimal Paid { get; init; }

    public required decimal Outstanding { get; init; }

    public required string Currency { get; init; }

    /// <summary>Draft, Issued, Paid, Void or Overdue.</summary>
    public required string Status { get; init; }

    public DateTimeOffset? IssuedAt { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public DateTimeOffset? PaidAt { get; init; }

    public required IReadOnlyCollection<InvoiceLineResponse> Lines { get; init; }

    public required IReadOnlyCollection<PaymentResponse> Payments { get; init; }
}

// --- Access and overview -------------------------------------------------

public sealed record AccountResponse
{
    public required Guid Id { get; init; }

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Null for platform staff.</summary>
    public Guid? CustomerId { get; init; }

    public string? CustomerName { get; init; }

    /// <summary>Platform or Customer, derived from whether the account belongs to a customer.</summary>
    public required string Scope { get; init; }

    /// <summary>Role names, for display.</summary>
    public required IReadOnlyCollection<string> Roles { get; init; }

    /// <summary>
    /// The same roles by id, which is what setting them takes.
    ///
    /// Both, because names are what an operator reads and ids are what the API
    /// accepts - and a client left to map one to the other by name would pick
    /// the wrong role the first time a platform and a customer role shared one.
    /// </summary>
    public required IReadOnlyCollection<Guid> RoleIds { get; init; }

    public required string Status { get; init; }

    public required bool MfaEnabled { get; init; }

    public DateTimeOffset? LastLoginAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record RoleResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Platform or Customer.</summary>
    public required string Scope { get; init; }

    public required bool IsSystem { get; init; }

    public Guid? CustomerId { get; init; }

    public required IReadOnlyCollection<string> Permissions { get; init; }

    public required int PermissionCount { get; init; }

    /// <summary>How many accounts hold this role.</summary>
    public required int UserCount { get; init; }
}

public sealed record CustomerCountsResponse
{
    public required int Total { get; init; }
    public required int Active { get; init; }
    public required int Suspended { get; init; }
    public required int Prospect { get; init; }
    public required int Archived { get; init; }
}

public sealed record StoreCountsResponse
{
    public required int Total { get; init; }
    public required int Connected { get; init; }
    public required int Degraded { get; init; }
    public required int Disconnected { get; init; }
    public required int NotRegistered { get; init; }
}

public sealed record SubscriptionCountsResponse
{
    public required int Total { get; init; }
    public required int Active { get; init; }
    public required int Trial { get; init; }
    public required int PastDue { get; init; }
    public required int Suspended { get; init; }
    public required int ActiveEntitlements { get; init; }
}

public sealed record BillingCountsResponse
{
    public required int Draft { get; init; }
    public required int Issued { get; init; }
    public required int Overdue { get; init; }
    public required int Paid { get; init; }
    public required decimal OutstandingTotal { get; init; }

    /// <summary>Null when nothing is outstanding, or when more than one currency is.</summary>
    public string? Currency { get; init; }
}

public sealed record ActivityResponse
{
    public required Guid Id { get; init; }
    public required string Action { get; init; }
    public required string TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? Actor { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// The dashboard's landing figures. Subsystems that do not exist yet — server
/// metrics, alerts, feature delivery — are absent rather than reported as zeros
/// that would look like real measurements.
/// </summary>
public sealed record OverviewResponse
{
    public required CustomerCountsResponse Customers { get; init; }
    public required StoreCountsResponse Stores { get; init; }
    public required SubscriptionCountsResponse Subscriptions { get; init; }
    public required BillingCountsResponse Billing { get; init; }
    public required IReadOnlyCollection<ActivityResponse> RecentActivity { get; init; }
}
