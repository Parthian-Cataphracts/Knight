using Observability.Domain;

namespace Observability;

/// <summary>
/// Channels, and the deliveries that go through them.
///
/// Queueing and sending are separate operations on purpose. The thing that
/// notices a problem must not wait on an SMTP handshake, and a notification that
/// was never queued cannot be retried — so the detection path writes a row and
/// returns, and the dispatcher owns everything that can be slow or fail.
/// </summary>
public interface INotificationService
{
    Task<IReadOnlyCollection<NotificationChannel>> ListChannelsAsync(
        Guid? customerId,
        bool includeDisabled,
        CancellationToken cancellationToken);

    Task<NotificationChannel> GetChannelAsync(Guid id, CancellationToken cancellationToken);

    Task<NotificationChannel> CreateChannelAsync(
        Guid? customerId,
        string name,
        NotificationChannelKind kind,
        string? endpoint,
        NotificationSeverity minimumSeverity,
        IReadOnlyCollection<string>? ruleFilter,
        string? secret,
        CancellationToken cancellationToken);

    Task<NotificationChannel> UpdateChannelAsync(
        Guid id,
        string name,
        NotificationSeverity minimumSeverity,
        IReadOnlyCollection<string>? ruleFilter,
        CancellationToken cancellationToken);

    Task<NotificationChannel> SetChannelEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a test notification through one channel, so an operator can find out
    /// their webhook is wrong now rather than during the outage it was configured
    /// for.
    /// </summary>
    Task<NotificationSendResult> TestChannelAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<NotificationDelivery> Items, long TotalCount)> ListDeliveriesAsync(
        Guid? channelId,
        NotificationDeliveryStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task MarkReadAsync(Guid deliveryId, CancellationToken cancellationToken);

    /// <summary>
    /// Fans one notification out to every channel that wants it, and returns how
    /// many were queued. Zero is a legitimate answer and worth logging: it means
    /// something went wrong and nobody asked to hear about it.
    /// </summary>
    Task<int> QueueAsync(
        NotificationSeverity severity,
        string ruleKey,
        NotificationSubject subject,
        Guid subjectId,
        Guid? customerId,
        string title,
        string body,
        CancellationToken cancellationToken);

    /// <summary>Attempts every delivery that is due, and answers how many were sent.</summary>
    Task<NotificationDispatchResult> DispatchDueAsync(CancellationToken cancellationToken);
}

/// <summary>What one dispatch pass did. Reported rather than logged so a test can assert on it.</summary>
public sealed record NotificationDispatchResult(int Attempted, int Delivered, int Failed, int ChannelsDisabled);
