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

    /// <summary>Draft, Published, Deprecated or Withdrawn.</summary>
    public required string Status { get; init; }

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

    public required IReadOnlyCollection<PlanFeatureResponse> Features { get; init; }

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

    public required Guid PlanId { get; init; }

    /// <summary>Trial, Active, PastDue, Suspended or Cancelled.</summary>
    public required string Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CurrentPeriodStart { get; init; }

    public required DateTimeOffset CurrentPeriodEnd { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public required IReadOnlyCollection<SubscriptionFeatureResponse> Features { get; init; }
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
