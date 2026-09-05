using AutoAdmin.Domain;

namespace AutoAdmin;

/// <summary>
/// The Automatic Admin orchestrator: the "give it a topic and it does everything"
/// brain (docs/adr/0038). It generates content for the parts a customer is
/// entitled to, then — per the customer's autonomy — drafts it for approval (the
/// default) or publishes it, and reports back. It only ever acts on the parts the
/// customer bought; entitlement is the gate, never a client-sent value.
/// </summary>
public interface IAutoAdminService
{
    /// <summary>The customer's settings, created with the safe default on first access.</summary>
    Task<AutoAdminSettings> GetSettingsAsync(Guid customerId, CancellationToken cancellationToken);

    Task<AutoAdminSettings> SetAutonomyAsync(Guid customerId, AutonomyMode autonomy, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the admin on a topic: generates content for the entitled generation
    /// parts, and — if the customer is on full-auto — publishes it to the entitled
    /// channels straight away, otherwise leaves it as a draft to approve. Refused
    /// if the customer holds no generation part, because there would be nothing to
    /// make.
    /// </summary>
    Task<ContentJob> SubmitAsync(Guid customerId, string topic, CancellationToken cancellationToken);

    /// <summary>Approves a draft run and publishes it to the entitled channels.</summary>
    Task<ContentJob> ApproveAsync(Guid customerId, Guid jobId, CancellationToken cancellationToken);

    Task<ContentJob?> GetJobAsync(Guid customerId, Guid jobId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ContentJob>> ListJobsAsync(Guid customerId, CancellationToken cancellationToken);
}
