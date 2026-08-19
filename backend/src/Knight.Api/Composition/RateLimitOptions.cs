namespace Knight.Api.Composition;

/// <summary>
/// Bound from configuration (section "RateLimiting"). Kept configurable rather
/// than hardcoded so integration tests (where many unrelated test cases share
/// one apparent client IP under an in-process TestServer) can raise these
/// limits, while a dedicated rate-limit test can lower them for just that run —
/// see docs/architecture/authorization.md.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Requests per window for the shared "platform" (authenticated administration) policy.</summary>
    public int PlatformPermitLimit { get; init; } = 100;

    /// <summary>Requests per window for the shared "tenant-public" (anonymous storefront) policy.</summary>
    public int TenantPublicPermitLimit { get; init; } = 300;

    public int PlatformLoginPermitLimit { get; init; } = 10;

    public int TenantLoginPermitLimit { get; init; } = 10;

    public int RefreshPermitLimit { get; init; } = 30;

    public int CheckoutQuotePermitLimit { get; init; } = 60;

    public int CheckoutSubmitPermitLimit { get; init; } = 20;

    /// <summary>Requests per window for authenticated control-plane (dashboard) traffic.</summary>
    public int ControlPlanePermitLimit { get; init; } = 200;

    /// <summary>
    /// Requests per window for control-plane login and refresh. Kept low on
    /// purpose: it is the account-lockout rule's partner, throttling a source
    /// rather than an account (docs/authentication.md section 1).
    /// </summary>
    public int ControlPlaneLoginPermitLimit { get; init; } = 10;

    /// <summary>
    /// Handshakes per window from one address. A store handshakes twice an hour;
    /// anything doing it thirty times a minute is guessing credentials
    /// (docs/authentication.md section 2).
    /// </summary>
    public int IngestHandshakePermitLimit { get; init; } = 30;

    /// <summary>
    /// Ingestion requests per window for one store. Generous enough for a busy
    /// store batching errors and heartbeating, bounded so a store in a crash loop
    /// cannot spend the control plane's capacity.
    /// </summary>
    public int IngestPermitLimit { get; init; } = 600;

    public int WindowSeconds { get; init; } = 60;
}
