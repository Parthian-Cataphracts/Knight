using Knight.Domain.Exceptions;
using Observability.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The incident lifecycle and its timeline.
///
/// The timeline is the reason incidents exist at all, so most of what is asserted
/// here is that it records what happened, in order, and cannot be got rid of.
/// </summary>
public sealed class IncidentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OpeningIsItselfTheFirstTimelineEntry()
    {
        // No incident can exist without one: an incident with an empty timeline
        // would be a record of nothing.
        var incident = OpenManually();

        var entry = Assert.Single(incident.Timeline);

        Assert.Equal(IncidentEventType.Opened, entry.Type);
        Assert.Equal(IncidentStatus.Open, incident.Status);
    }

    [Fact]
    public void ARuleOpenedIncidentRecordsTheRuleAndNoActor()
    {
        var incident = Incident.Open(
            Guid.NewGuid(), Now, "INC-2026-0002", "Install failed", IncidentSeverity.Critical,
            ruleKey: "feature.install.failed");

        Assert.Equal("feature.install.failed", incident.RuleKey);
        Assert.Null(incident.OpenedBy);
        Assert.Null(Assert.Single(incident.Timeline).ActorId);
    }

    [Fact]
    public void TheFullResponseAppearsOnTheTimelineInOrder()
    {
        var incident = OpenManually();
        var responder = Guid.NewGuid();

        incident.Acknowledge(responder, Now.AddMinutes(2), "Looking now.");
        incident.AddNote(responder, Now.AddMinutes(5), "Cause looks like the migration.");
        incident.Mitigate(responder, Now.AddMinutes(9), "Rolled the release back.");
        incident.Resolve(responder, Now.AddMinutes(30), "Migration 0003 was not reversible.");

        Assert.Equal(
            [
                IncidentEventType.Opened,
                IncidentEventType.StatusChanged,
                IncidentEventType.Note,
                IncidentEventType.Mitigated,
                IncidentEventType.Resolved,
            ],
            incident.Timeline.Select(entry => entry.Type));

        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.Equal(Now.AddMinutes(2), incident.AcknowledgedAt);
        Assert.Equal(Now.AddMinutes(9), incident.MitigatedAt);
        Assert.Equal("Migration 0003 was not reversible.", incident.RootCause);
    }

    [Fact]
    public void MitigationMustSayWhatMitigatedIt()
    {
        // An unexplained mitigation cannot be reviewed afterwards, which defeats
        // the point of recording it.
        var incident = OpenManually();

        Assert.Throws<DomainException>(() => incident.Mitigate(Guid.NewGuid(), Now, "   "));
    }

    [Fact]
    public void AcknowledgementTimeIsNotOverwrittenBySubsequentUpdates()
    {
        // Response time is the number that gets measured; the second responder
        // must not reset it.
        var incident = OpenManually();

        incident.Acknowledge(Guid.NewGuid(), Now.AddMinutes(2), null);
        incident.Acknowledge(Guid.NewGuid(), Now.AddMinutes(20), null);

        Assert.Equal(Now.AddMinutes(2), incident.AcknowledgedAt);
    }

    [Fact]
    public void WorkingAResolvedIncidentIsRefusedUntilItIsReopened()
    {
        var incident = OpenManually();
        var responder = Guid.NewGuid();

        incident.Resolve(responder, Now.AddMinutes(10), null);

        Assert.Throws<DomainException>(() => incident.Acknowledge(responder, Now.AddMinutes(11), null));
        Assert.Throws<DomainException>(() => incident.Mitigate(responder, Now.AddMinutes(11), "note"));
    }

    [Fact]
    public void ReopeningKeepsTheOriginalTimeline()
    {
        // The two halves are one story. Starting a second incident would lose the
        // first half of it.
        var incident = OpenManually();
        var responder = Guid.NewGuid();

        incident.Resolve(responder, Now.AddMinutes(10), null);
        incident.Reopen(responder, Now.AddMinutes(20), "It came back.");

        Assert.Equal(IncidentStatus.Investigating, incident.Status);
        Assert.Null(incident.ResolvedAt);
        Assert.Equal(3, incident.Timeline.Count);
    }

    [Fact]
    public void OnlyAResolvedIncidentCanBeReopened()
    {
        Assert.Throws<DomainException>(() => OpenManually().Reopen(Guid.NewGuid(), Now, "why"));
    }

    [Fact]
    public void ResolvingTwiceIsRefused()
    {
        var incident = OpenManually();
        var responder = Guid.NewGuid();

        incident.Resolve(responder, Now.AddMinutes(10), null);

        Assert.Throws<DomainException>(() => incident.Resolve(responder, Now.AddMinutes(11), null));
    }

    [Fact]
    public void SeverityEscalatesButNeverDeEscalates()
    {
        // An incident that quietly de-escalated itself while the impact continued
        // is the failure mode this guards against.
        var incident = Incident.Open(
            Guid.NewGuid(), Now, "INC-2026-0003", "Slow", IncidentSeverity.Warning, openedBy: Guid.NewGuid());

        incident.Escalate(IncidentSeverity.Critical, Now.AddMinutes(5), "Now failing outright.");
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);

        incident.Escalate(IncidentSeverity.Info, Now.AddMinutes(10), "Looks better.");
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
    }

    [Fact]
    public void AnIncidentMustSayWhatItIsAbout()
    {
        Assert.Throws<DomainException>(() => Incident.Open(
            Guid.NewGuid(), Now, "INC-2026-0004", "  ", IncidentSeverity.Critical));
    }

    private static Incident OpenManually() => Incident.Open(
        Guid.NewGuid(), Now, "INC-2026-0001", "Checkout is failing", IncidentSeverity.Critical,
        openedBy: Guid.NewGuid());
}

/// <summary>
/// Notification routing, retry and the channel circuit breaker.
///
/// The failure mode of a notification system is not silence — it is being
/// ignored. Nearly everything asserted here is a rule that exists to keep the
/// volume honest.
/// </summary>
public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AChannelRefusesSeveritiesBelowItsFloor()
    {
        var channel = Webhook(NotificationSeverity.Critical);

        Assert.True(channel.Accepts(NotificationSeverity.Critical, "server.offline"));
        Assert.False(channel.Accepts(NotificationSeverity.Warning, "server.offline"));
        Assert.False(channel.Accepts(NotificationSeverity.Info, "server.offline"));
    }

    [Fact]
    public void AnEmptyRuleFilterMeansEveryRule()
    {
        var channel = Webhook(NotificationSeverity.Info);

        Assert.True(channel.Accepts(NotificationSeverity.Info, "anything.at.all"));
    }

    [Fact]
    public void ARuleFilterExcludesEverythingElse()
    {
        var channel = Webhook(NotificationSeverity.Info, ["server.offline", "feature.drift"]);

        Assert.True(channel.Accepts(NotificationSeverity.Info, "feature.drift"));
        Assert.False(channel.Accepts(NotificationSeverity.Info, "errors.spike"));
    }

    [Fact]
    public void ADisabledChannelAcceptsNothing()
    {
        var channel = Webhook(NotificationSeverity.Info);

        channel.Disable("by hand", Now);

        Assert.False(channel.Accepts(NotificationSeverity.Critical, "server.offline"));
    }

    [Fact]
    public void AnInAppChannelHasNoDestination()
    {
        // Accepting one would imply it went somewhere outside KNIGHT.
        var channel = NotificationChannel.Create(
            Guid.NewGuid(), Now, null, "In app", NotificationChannelKind.InApp,
            "https://example.com/ignored", NotificationSeverity.Info);

        Assert.Null(channel.Endpoint);
    }

    [Fact]
    public void AWebhookNeedsAnAbsoluteHttpUrl()
    {
        Assert.Throws<DomainException>(() => NotificationChannel.Create(
            Guid.NewGuid(), Now, null, "Bad", NotificationChannelKind.Webhook,
            "not-a-url", NotificationSeverity.Info));

        Assert.Throws<DomainException>(() => NotificationChannel.Create(
            Guid.NewGuid(), Now, null, "Bad", NotificationChannelKind.Webhook,
            "ftp://example.com/hook", NotificationSeverity.Info));
    }

    [Fact]
    public void AnEmailChannelNeedsSomethingLikeAnAddress()
    {
        Assert.Throws<DomainException>(() => NotificationChannel.Create(
            Guid.NewGuid(), Now, null, "Bad", NotificationChannelKind.Email,
            "nobody", NotificationSeverity.Info));
    }

    [Fact]
    public void RetryDelaysGrowExponentiallyAndAreCapped()
    {
        var delivery = Queue();

        delivery.BeginAttempt(Now);
        delivery.MarkFailed("nope", 10, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), Now);
        Assert.Equal(Now.AddSeconds(30), delivery.NextAttemptAt);

        delivery.BeginAttempt(Now);
        delivery.MarkFailed("nope", 10, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), Now);
        Assert.Equal(Now.AddSeconds(60), delivery.NextAttemptAt);

        delivery.BeginAttempt(Now);
        delivery.MarkFailed("nope", 10, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), Now);
        Assert.Equal(Now.AddSeconds(120), delivery.NextAttemptAt);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            delivery.BeginAttempt(Now);
            delivery.MarkFailed("nope", 20, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), Now);
        }

        // Capped: a persistent failure must not become a denial of service
        // against whoever is on the other end.
        Assert.Equal(Now.AddMinutes(30), delivery.NextAttemptAt);
    }

    [Fact]
    public void ADeliveryIsGivenUpOnAfterTheAttemptLimit()
    {
        var delivery = Queue();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            delivery.BeginAttempt(Now);
            delivery.MarkFailed("nope", 3, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), Now);
        }

        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(3, delivery.AttemptCount);
        Assert.False(delivery.IsDue(Now.AddDays(1)));
    }

    [Fact]
    public void ADeliveredNotificationIsNeverAttemptedAgain()
    {
        var delivery = Queue();

        delivery.BeginAttempt(Now);
        delivery.MarkDelivered(Now);

        Assert.Throws<DomainException>(() => delivery.BeginAttempt(Now.AddMinutes(1)));
        Assert.False(delivery.IsDue(Now.AddDays(1)));
    }

    [Fact]
    public void AChannelIsSwitchedOffAfterEnoughConsecutiveFailures()
    {
        // A webhook that has rejected everything all week is not going to accept
        // the next one, and pretending otherwise hides that nobody has been
        // notified of anything.
        var channel = Webhook(NotificationSeverity.Info);

        for (var failure = 0; failure < 4; failure++)
        {
            Assert.False(channel.RecordFailure(5, "connection refused", Now));
        }

        Assert.True(channel.RecordFailure(5, "connection refused", Now));
        Assert.False(channel.IsEnabled);
        Assert.Contains("connection refused", channel.DisabledReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void OneSuccessClearsTheFailureRun()
    {
        var channel = Webhook(NotificationSeverity.Info);

        channel.RecordFailure(3, "flaky", Now);
        channel.RecordFailure(3, "flaky", Now);
        channel.RecordSuccess(Now);

        Assert.Equal(0, channel.ConsecutiveFailures);
        Assert.False(channel.RecordFailure(3, "flaky", Now));
        Assert.True(channel.IsEnabled);
    }

    [Fact]
    public void ReEnablingAChannelClearsItsFailureHistory()
    {
        var channel = Webhook(NotificationSeverity.Info);

        for (var failure = 0; failure < 5; failure++)
        {
            channel.RecordFailure(5, "down", Now);
        }

        channel.Enable(Now);

        Assert.True(channel.IsEnabled);
        Assert.Equal(0, channel.ConsecutiveFailures);
        Assert.Null(channel.DisabledReason);
    }

    [Fact]
    public void AnAbandonedDeliveryIsNotRetried()
    {
        var delivery = Queue();

        delivery.BeginAttempt(Now);
        delivery.Abandon("The endpoint answered 404.", Now);

        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.False(delivery.IsDue(Now.AddYears(1)));
    }

    [Fact]
    public void MarkingReadTwiceKeepsTheFirstTime()
    {
        var delivery = Queue();

        delivery.MarkRead(Now);
        delivery.MarkRead(Now.AddHours(1));

        Assert.Equal(Now, delivery.ReadAt);
    }

    private static NotificationChannel Webhook(
        NotificationSeverity minimum,
        IEnumerable<string>? rules = null) =>
        NotificationChannel.Create(
            Guid.NewGuid(), Now, null, "On call", NotificationChannelKind.Webhook,
            "https://hooks.example.com/knight", minimum, rules);

    private static NotificationDelivery Queue() => NotificationDelivery.Queue(
        Guid.NewGuid(), Now, Guid.NewGuid(), null, NotificationSeverity.Critical,
        "server.offline", NotificationSubject.Alert, Guid.NewGuid(),
        "web-01 has not reported for 5 minutes.", "body");
}
