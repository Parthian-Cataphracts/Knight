using Knight.Domain.Common;

namespace AutoAdmin.Domain;

/// <summary>
/// One customer's Automatic Admin settings. Autonomy is the one that matters:
/// it defaults to <see cref="AutonomyMode.ApprovalRequired"/>, because a wrong
/// post or a wrong reply is the merchant's liability, and full-auto is an opt-in
/// the customer takes deliberately (docs/adr/0038).
/// </summary>
public sealed class AutoAdminSettings : AuditableEntity
{
    public Guid CustomerId { get; private set; }

    public AutonomyMode Autonomy { get; private set; }

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
}
