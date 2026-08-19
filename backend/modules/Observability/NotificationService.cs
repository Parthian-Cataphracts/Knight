using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Security;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Observability.Domain;

namespace Observability;

/// <summary>
/// Tells people what happened, and consumes the alert stream that decides when
/// there is anything to tell.
///
/// Both responsibilities sit here because they are one decision seen from two
/// sides: an alert is only worth raising if somebody would want to know, and a
/// notification is only worth sending if some condition is genuinely true. The
/// shape that follows is deliberately conservative about volume — an alert that
/// is re-observed does not re-notify, a rule and subject pair is suppressed for
/// a cooldown, and a channel that keeps failing is switched off rather than
/// retried forever. Every one of those exists because the failure mode of a
/// notification system is not silence; it is being ignored.
/// </summary>
internal sealed class NotificationService : INotificationService, IAlertEventPublisher
{
    private readonly INotificationRepository _notifications;
    private readonly INotificationTransport _transport;
    private readonly IIncidentService _incidents;
    private readonly IRealtimeNotifier _realtime;
    private readonly ISecretProtector _secrets;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<NotificationService> _logger;
    private readonly ObservabilityOptions _options;

    public NotificationService(
        INotificationRepository notifications,
        INotificationTransport transport,
        IIncidentService incidents,
        IRealtimeNotifier realtime,
        ISecretProtector secrets,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ILogger<NotificationService> logger,
        IOptions<ObservabilityOptions> options)
    {
        _notifications = notifications;
        _transport = transport;
        _incidents = incidents;
        _realtime = realtime;
        _secrets = secrets;
        _audit = audit;
        _clock = clock;
        _logger = logger;
        _options = options.Value;
    }

    // --- Channels ------------------------------------------------------------

    public Task<IReadOnlyCollection<NotificationChannel>> ListChannelsAsync(
        Guid? customerId,
        bool includeDisabled,
        CancellationToken cancellationToken) =>
        _notifications.ListChannelsAsync(customerId, includeDisabled, cancellationToken);

    public async Task<NotificationChannel> GetChannelAsync(Guid id, CancellationToken cancellationToken) =>
        await _notifications.GetChannelAsync(id, cancellationToken)
        ?? throw new NotFoundException("Notification channel", id);

    public async Task<NotificationChannel> CreateChannelAsync(
        Guid? customerId,
        string name,
        NotificationChannelKind kind,
        string? endpoint,
        NotificationSeverity minimumSeverity,
        IReadOnlyCollection<string>? ruleFilter,
        string? secret,
        CancellationToken cancellationToken)
    {
        RequireKnownRules(ruleFilter);

        var channel = NotificationChannel.Create(
            Guid.NewGuid(),
            _clock.UtcNow,
            customerId,
            name,
            kind,
            endpoint,
            minimumSeverity,
            ruleFilter,
            // Encrypted here rather than in the aggregate: the domain knows a
            // secret exists, and deliberately not how it is protected.
            string.IsNullOrWhiteSpace(secret) ? null : _secrets.Protect(secret));

        await _notifications.AddChannelAsync(channel, cancellationToken);
        await _notifications.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "notification.channel.created",
            "NotificationChannel",
            channel.Id.ToString(),
            customerId,
            cancellationToken,
            // The endpoint is recorded; the secret never is. The audit trail is
            // the last place a credential should end up.
            newValue: new { channel.Name, channel.Kind, channel.Endpoint, channel.MinimumSeverity });

        return channel;
    }

    public async Task<NotificationChannel> UpdateChannelAsync(
        Guid id,
        string name,
        NotificationSeverity minimumSeverity,
        IReadOnlyCollection<string>? ruleFilter,
        CancellationToken cancellationToken)
    {
        RequireKnownRules(ruleFilter);

        var channel = await GetChannelAsync(id, cancellationToken);

        channel.Update(name, minimumSeverity, ruleFilter, _clock.UtcNow);

        await _notifications.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "notification.channel.updated",
            "NotificationChannel",
            channel.Id.ToString(),
            channel.CustomerId,
            cancellationToken,
            newValue: new { channel.Name, channel.MinimumSeverity, channel.RuleFilter });

        return channel;
    }

    public async Task<NotificationChannel> SetChannelEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        var channel = await GetChannelAsync(id, cancellationToken);
        var now = _clock.UtcNow;

        if (enabled)
        {
            channel.Enable(now);
        }
        else
        {
            channel.Disable("Disabled by an operator.", now);
        }

        await _notifications.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            enabled ? "notification.channel.enabled" : "notification.channel.disabled",
            "NotificationChannel",
            channel.Id.ToString(),
            channel.CustomerId,
            cancellationToken);

        return channel;
    }

    public async Task<NotificationSendResult> TestChannelAsync(Guid id, CancellationToken cancellationToken)
    {
        var channel = await GetChannelAsync(id, cancellationToken);
        var now = _clock.UtcNow;

        var probe = NotificationDelivery.Queue(
            Guid.NewGuid(),
            now,
            channel.Id,
            channel.CustomerId,
            NotificationSeverity.Info,
            "notification.test",
            NotificationSubject.Alert,
            channel.Id,
            "KNIGHT test notification",
            "This is a test. If you are reading it, this channel works.");

        probe.BeginAttempt(now);

        var result = await SendSafelyAsync(channel, probe, cancellationToken);

        if (result.Succeeded)
        {
            probe.MarkDelivered(_clock.UtcNow);
            channel.RecordSuccess(_clock.UtcNow);
        }
        else
        {
            // A failed test is abandoned rather than retried: the operator is
            // standing there watching, and will press the button again.
            probe.Abandon(result.Error ?? "Unknown error", _clock.UtcNow);
        }

        await _notifications.AddDeliveryAsync(probe, cancellationToken);
        await _notifications.SaveChangesAsync(cancellationToken);

        return result;
    }

    private static void RequireKnownRules(IReadOnlyCollection<string>? rules)
    {
        if (rules is null)
        {
            return;
        }

        // A filter naming a rule that does not exist silently matches nothing,
        // which looks exactly like a channel that works and is never used.
        var unknown = rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule))
            .Select(rule => rule.Trim())
            .Where(rule => !ObservabilityRules.All.Contains(rule, StringComparer.OrdinalIgnoreCase) &&
                           !KnownExternalRules.Contains(rule, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (unknown.Length > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["ruleFilter"] = [$"Unknown rule key(s): {string.Join(", ", unknown)}."],
            });
        }
    }

    /// <summary>
    /// Rules raised by other modules that a channel may legitimately filter on.
    /// Duplicated here as strings rather than referenced, because taking a
    /// dependency on the module that owns them to read six constants would be a
    /// worse trade than this list going stale.
    /// </summary>
    private static readonly string[] KnownExternalRules =
    [
        "server.offline",
        "server.degraded",
        "server.disk.critical",
        "agent.offline",
        "store.unreachable",
        "notification.test",
    ];

    // --- Deliveries ----------------------------------------------------------

    public Task<(IReadOnlyCollection<NotificationDelivery> Items, long TotalCount)> ListDeliveriesAsync(
        Guid? channelId,
        NotificationDeliveryStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        _notifications.ListDeliveriesAsync(
            channelId,
            status,
            Math.Max(page, 1),
            Math.Clamp(pageSize, 1, 200),
            cancellationToken);

    public async Task MarkReadAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await _notifications.GetDeliveryAsync(deliveryId, cancellationToken)
            ?? throw new NotFoundException("Notification", deliveryId);

        delivery.MarkRead(_clock.UtcNow);

        await _notifications.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> QueueAsync(
        NotificationSeverity severity,
        string ruleKey,
        NotificationSubject subject,
        Guid subjectId,
        Guid? customerId,
        string title,
        string body,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var channels = await _notifications.ListRoutableAsync(customerId, cancellationToken);
        var cutoff = now - _options.NotificationCooldown;
        var queued = new List<NotificationDelivery>();

        foreach (var channel in channels.Where(channel => channel.Accepts(severity, ruleKey)))
        {
            // The cooldown is per channel rather than global: a webhook feeding a
            // paging rota and an in-app list have genuinely different tolerances
            // for repetition, and the channel is where that preference lives.
            if (await _notifications.HasRecentAsync(channel.Id, ruleKey, subjectId, cutoff, cancellationToken))
            {
                continue;
            }

            queued.Add(NotificationDelivery.Queue(
                Guid.NewGuid(),
                now,
                channel.Id,
                channel.CustomerId,
                severity,
                ruleKey,
                subject,
                subjectId,
                title,
                body));
        }

        if (queued.Count == 0)
        {
            _logger.LogDebug(
                "Nothing was notified for rule {RuleKey} on subject {SubjectId}: no channel wanted it.",
                ruleKey,
                subjectId);

            return 0;
        }

        await _notifications.AddDeliveriesAsync(queued, cancellationToken);
        await _notifications.SaveChangesAsync(cancellationToken);

        return queued.Count;
    }

    public async Task<NotificationDispatchResult> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var due = await _notifications.ListDueAsync(now, _options.DispatchBatchSize, cancellationToken);

        if (due.Count == 0)
        {
            return new NotificationDispatchResult(0, 0, 0, 0);
        }

        var delivered = 0;
        var failed = 0;
        var disabled = 0;

        foreach (var delivery in due)
        {
            var channel = await _notifications.GetChannelAsync(delivery.ChannelId, cancellationToken);

            if (channel is null)
            {
                // The channel was deleted between queueing and sending. The
                // delivery is closed rather than retried forever against nothing.
                delivery.Abandon("The channel no longer exists.", _clock.UtcNow);
                failed++;

                continue;
            }

            if (!channel.IsEnabled)
            {
                delivery.Abandon($"The channel is disabled: {channel.DisabledReason}", _clock.UtcNow);
                failed++;

                continue;
            }

            delivery.BeginAttempt(_clock.UtcNow);

            var result = await SendSafelyAsync(channel, delivery, cancellationToken);

            if (result.Succeeded)
            {
                delivery.MarkDelivered(_clock.UtcNow);
                channel.RecordSuccess(_clock.UtcNow);
                delivered++;

                continue;
            }

            if (result.Permanent)
            {
                delivery.Abandon(result.Error ?? "Permanent failure.", _clock.UtcNow);
            }
            else
            {
                delivery.MarkFailed(
                    result.Error ?? "Unknown error",
                    _options.MaxDeliveryAttempts,
                    _options.RetryBaseDelay,
                    _options.MaxRetryDelay,
                    _clock.UtcNow);
            }

            failed++;

            if (channel.RecordFailure(_options.ChannelFailureThreshold, result.Error ?? "Unknown error", _clock.UtcNow))
            {
                disabled++;

                _logger.LogError(
                    "Notification channel {ChannelId} ({ChannelName}) was disabled after {Failures} consecutive failures.",
                    channel.Id,
                    channel.Name,
                    channel.ConsecutiveFailures);
            }
        }

        await _notifications.SaveChangesAsync(cancellationToken);

        return new NotificationDispatchResult(due.Count, delivered, failed, disabled);
    }

    /// <summary>
    /// Sends, and converts anything the transport throws into a transient
    /// failure. A dispatcher that dies on one bad webhook stops delivering for
    /// everybody, which is a far larger fault than the one it was reporting.
    /// </summary>
    private async Task<NotificationSendResult> SendSafelyAsync(
        NotificationChannel channel,
        NotificationDelivery delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _transport.SendAsync(channel, delivery, cancellationToken);

            if (channel.Kind is NotificationChannelKind.InApp && result.Succeeded)
            {
                await PushAsync(delivery, cancellationToken);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The transport for channel {ChannelId} ({Kind}) threw while sending delivery {DeliveryId}.",
                channel.Id,
                channel.Kind,
                delivery.Id);

            return NotificationSendResult.Transient(exception.Message);
        }
    }

    private async Task PushAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            await _realtime.BroadcastAsync(
                new RealtimeMessage(
                    "notificationReceived",
                    delivery.CustomerId,
                    new
                    {
                        id = delivery.Id,
                        severity = delivery.Severity.ToString(),
                        ruleKey = delivery.RuleKey,
                        title = delivery.Title,
                        body = delivery.Body,
                        subject = delivery.Subject.ToString(),
                        subjectId = delivery.SubjectId,
                    }),
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to push in-app notification {DeliveryId}.", delivery.Id);
        }
    }

    // --- The alert stream ----------------------------------------------------

    /// <summary>
    /// An alert was raised somewhere in the control plane.
    ///
    /// Only a genuinely new alert notifies. A re-observation means the same thing
    /// is still true, and telling somebody once a minute that a server is still
    /// offline is how a channel becomes a filter rule in somebody's mail client.
    /// </summary>
    public async Task PublishAsync(AlertRaised @event, CancellationToken cancellationToken)
    {
        if (!@event.IsNew)
        {
            return;
        }

        var severity = ParseSeverity(@event.Severity);

        if (_options.OpenIncidentsAutomatically && severity is NotificationSeverity.Critical)
        {
            // Critical only. An incident is a claim that people are responding,
            // and opening one for every warning devalues the word until nobody
            // reacts to either.
            await OpenIncidentSafelyAsync(@event, cancellationToken);
        }

        var queued = await QueueAsync(
            severity,
            @event.RuleKey,
            NotificationSubject.Alert,
            @event.AlertId,
            @event.CustomerId,
            @event.Message,
            $"Rule {@event.RuleKey} fired on {@event.Source} {@event.SourceId}.",
            cancellationToken);

        _logger.LogInformation(
            "Alert {RuleKey} on {Source} {SourceId} queued {Count} notification(s).",
            @event.RuleKey,
            @event.Source,
            @event.SourceId,
            queued);
    }

    public async Task PublishAsync(AlertResolved @event, CancellationToken cancellationToken)
    {
        // Recovery is notified without a cooldown check of its own — the
        // cooldown keys on rule and subject, and a recovery for a subject that
        // was just alerted on is precisely the message somebody is waiting for.
        await QueueAsync(
            NotificationSeverity.Info,
            @event.RuleKey,
            NotificationSubject.Alert,
            @event.AlertId,
            @event.CustomerId,
            $"Resolved: {@event.Message}",
            $"The condition behind {@event.RuleKey} has cleared.",
            cancellationToken);
    }

    private async Task OpenIncidentSafelyAsync(AlertRaised @event, CancellationToken cancellationToken)
    {
        try
        {
            await _incidents.OpenFromRuleAsync(
                @event.RuleKey,
                @event.SourceId,
                @event.Message,
                IncidentSeverity.Critical,
                @event.CustomerId,
                storeId: string.Equals(@event.Source, "Store", StringComparison.OrdinalIgnoreCase) ? @event.SourceId : null,
                serverId: string.Equals(@event.Source, "Server", StringComparison.OrdinalIgnoreCase) ? @event.SourceId : null,
                detail: @event.Message,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Failing to open the incident must not stop the notification: being
            // told is more urgent than the record of being told.
            _logger.LogError(
                exception,
                "Failed to open an incident for alert {AlertId} ({RuleKey}).",
                @event.AlertId,
                @event.RuleKey);
        }
    }

    private static NotificationSeverity ParseSeverity(string severity) =>
        Enum.TryParse<NotificationSeverity>(severity, ignoreCase: true, out var parsed)
            ? parsed
            : NotificationSeverity.Warning;
}
