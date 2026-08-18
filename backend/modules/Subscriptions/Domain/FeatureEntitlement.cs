using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Subscriptions.Domain;

/// <summary>
/// The commercial fact that a customer is owed a capability.
///
/// This is deliberately an explicit record rather than something derived on the
/// fly from a plan, because the three ways a customer can come to hold a feature
/// are genuinely different and each needs its own audit trail: it came with the
/// plan, they chose it, or someone granted it by hand.
///
/// **An entitlement is not an installation.** Granting one triggers delivery; it
/// does not by itself make the capability exist in any store. Losing one
/// disables the installed code — it does not uninstall it, and it does not
/// delete data ([`adr/0016`](../../../docs/adr/0016-feature-migration-and-removal-policy.md)).
/// </summary>
public sealed class FeatureEntitlement : Entity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public Guid FeatureId { get; private set; }

    public EntitlementSource Source { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    /// <summary>Set for time-boxed grants, such as a trial of a paid capability.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    /// <summary>Set when a person granted it by hand, so a grant is always attributable.</summary>
    public Guid? GrantedBy { get; private set; }

    private FeatureEntitlement()
    {
    }

    private FeatureEntitlement(
        Guid id,
        Guid customerId,
        Guid featureId,
        EntitlementSource source,
        DateTimeOffset grantedAt,
        DateTimeOffset? expiresAt,
        Guid? grantedBy)
        : base(id)
    {
        CustomerId = customerId;
        FeatureId = featureId;
        Source = source;
        GrantedAt = grantedAt;
        ExpiresAt = expiresAt;
        GrantedBy = grantedBy;
    }

    public static FeatureEntitlement Grant(
        Guid id,
        Guid customerId,
        Guid featureId,
        EntitlementSource source,
        DateTimeOffset grantedAt,
        DateTimeOffset? expiresAt = null,
        Guid? grantedBy = null)
    {
        if (customerId == Guid.Empty)
        {
            throw DomainException.Validation("An entitlement must belong to a customer.");
        }

        if (featureId == Guid.Empty)
        {
            throw DomainException.Validation("An entitlement must name a feature.");
        }

        if (expiresAt is not null && expiresAt <= grantedAt)
        {
            throw DomainException.Validation("An entitlement cannot expire before it is granted.");
        }

        if (source is EntitlementSource.Grant && grantedBy is null)
        {
            throw DomainException.Validation("A manual grant must record who made it.");
        }

        return new FeatureEntitlement(id, customerId, featureId, source, grantedAt, expiresAt, grantedBy);
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw DomainException.Validation("Revoking an entitlement requires a reason.");
        }

        RevokedAt = now;
        RevokedReason = reason.Trim();
    }

    /// <summary>Extends a time-boxed grant. Never shortens one — that is a revocation, and is recorded as such.</summary>
    public void ExtendTo(DateTimeOffset expiresAt)
    {
        if (RevokedAt is not null)
        {
            throw DomainException.Conflict("A revoked entitlement cannot be extended.");
        }

        if (ExpiresAt is not null && expiresAt <= ExpiresAt)
        {
            throw DomainException.Validation("An extension must move the expiry later.");
        }

        ExpiresAt = expiresAt;
    }

    public bool IsActiveAt(DateTimeOffset moment) =>
        RevokedAt is null && GrantedAt <= moment && (ExpiresAt is null || ExpiresAt > moment);
}

/// <summary>
/// How the customer came to hold the entitlement. It decides what happens when
/// the subscription changes: a plan entitlement follows the plan, a chosen one
/// follows the customer's selection, and a manual grant survives both until
/// someone revokes it explicitly.
/// </summary>
public enum EntitlementSource
{
    Plan = 0,
    Optional = 1,
    Grant = 2,
}
