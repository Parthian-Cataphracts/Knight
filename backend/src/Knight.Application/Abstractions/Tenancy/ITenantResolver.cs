using System.Security.Claims;

namespace Knight.Application.Abstractions.Tenancy;

/// <summary>
/// Resolves the tenant that applies to an inbound request from framework-agnostic
/// signals. Implementations may consult custom domains, subdomains, or trusted
/// claims; presentation code is responsible for populating <see cref="TenantResolutionRequest"/>
/// and must not resolve tenancy itself.
/// </summary>
public interface ITenantResolver
{
    Task<TenantResolutionResult> ResolveAsync(TenantResolutionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Framework-agnostic signals that can be used to resolve the current tenant.
/// </summary>
/// <param name="Host">The request host, when resolution by custom domain/subdomain applies.</param>
/// <param name="Principal">The authenticated principal, when resolution by token claims applies.</param>
public sealed record TenantResolutionRequest(string? Host, ClaimsPrincipal? Principal);

public enum TenantResolutionOutcome
{
    /// <summary>No tenant signal was present; the request proceeds without tenant context.</summary>
    NotResolved,

    /// <summary>A tenant was unambiguously identified and is active.</summary>
    Resolved,

    /// <summary>
    /// A tenant was identified but is not in a state that may serve requests
    /// (e.g. suspended or archived). Callers must reject the request rather than
    /// silently proceeding.
    /// </summary>
    Blocked,

    /// <summary>
    /// The authenticated token's tenant claim and the host-resolved tenant disagree.
    /// Callers must reject the request — this is never resolved by preferring one
    /// signal over the other.
    /// </summary>
    Conflict
}

public sealed record TenantResolutionResult
{
    public required TenantResolutionOutcome Outcome { get; init; }

    public Guid? TenantId { get; init; }

    public string? FailureReason { get; init; }

    public static TenantResolutionResult Resolved(Guid tenantId) =>
        new() { Outcome = TenantResolutionOutcome.Resolved, TenantId = tenantId };

    public static TenantResolutionResult NotResolved(string reason) =>
        new() { Outcome = TenantResolutionOutcome.NotResolved, FailureReason = reason };

    public static TenantResolutionResult Blocked(Guid tenantId, string reason) =>
        new() { Outcome = TenantResolutionOutcome.Blocked, TenantId = tenantId, FailureReason = reason };

    public static TenantResolutionResult Conflict(string reason) =>
        new() { Outcome = TenantResolutionOutcome.Conflict, FailureReason = reason };
}
