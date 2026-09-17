using Knight.Domain.Common;

namespace AutoAdmin.Domain;

/// <summary>
/// One customer's Automatic Admin settings. Autonomy is the one that matters:
/// it defaults to <see cref="AutonomyMode.ApprovalRequired"/>, because a wrong
/// post or a wrong reply is the merchant's liability, and full-auto is an opt-in
/// the customer takes deliberately (docs/adr/0038).
/// </summary>
public sealed class AutoAdminSettings : AuditableEntity, ICustomerOwned
{
    public Guid CustomerId { get; private set; }

    public AutonomyMode Autonomy { get; private set; }

    /// <summary>Whether the admin answers customer messages/comments on its own.
    /// Only meaningful when the customer holds the <c>auto-admin-autoreply</c>
    /// part; the service refuses to turn it on otherwise.</summary>
    public bool AutoReplyEnabled { get; private set; }

    /// <summary>Whether the admin promotes/boosts published content. Gated on the
    /// <c>auto-admin-boost</c> part the same way.</summary>
    public bool BoostEnabled { get; private set; }

    private AutoAdminSettings()
    {
    }

    private AutoAdminSettings(Guid id, DateTimeOffset createdAt, Guid customerId, AutonomyMode autonomy)
        : base(id, createdAt)
    {
        CustomerId = customerId;
        Autonomy = autonomy;
    }

    /// <summary>Creates settings for a customer, defaulting to approval-first.</summary>
    public static AutoAdminSettings CreateDefault(Guid id, DateTimeOffset createdAt, Guid customerId) =>
        new(id, createdAt, customerId, AutonomyMode.ApprovalRequired);

    public void SetAutonomy(AutonomyMode autonomy, DateTimeOffset now)
    {
        if (autonomy == Autonomy)
        {
            return;
        }

        Autonomy = autonomy;
        MarkUpdated(now);
    }

    /// <summary>
    /// Sets the behaviour toggles. The caller has already checked entitlement and
    /// passes <c>false</c> for a part the customer does not hold, so a lapsed
    /// entitlement can never leave a toggle stuck on.
    /// </summary>
    public void SetToggles(bool autoReply, bool boost, DateTimeOffset now)
    {
        if (autoReply == AutoReplyEnabled && boost == BoostEnabled)
        {
            return;
        }

        AutoReplyEnabled = autoReply;
        BoostEnabled = boost;
        MarkUpdated(now);
    }
}
