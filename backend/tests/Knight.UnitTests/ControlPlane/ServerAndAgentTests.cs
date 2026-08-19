using Knight.Domain.Exceptions;
using Servers.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Server status, agent enrolment and alert deduplication.
///
/// The enrolment tests carry the most weight. An agent runs on customer
/// infrastructure and installs code, so it is the highest-value target in the
/// system (risks.md R22), and a provisioning token that could be used twice would
/// be a way onto a second machine.
/// </summary>
public sealed class ServerAndAgentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private static Server NewServer() =>
        Server.Register(
            Guid.CreateVersion7(), Now, "web-01",
            ServerHostingModel.SharedManaged, ServerEnvironment.Production, "hetzner", "fsn1", "10.0.0.4");

    private static Agent NewAgent(Guid? serverId = null) =>
        Agent.Provision(Guid.CreateVersion7(), Now, serverId ?? Guid.CreateVersion7(), "hash-of-the-token");

    // --- Server ------------------------------------------------------------

    [Fact]
    public void ANewServerIsUnknownRatherThanHealthy()
    {
        // Optimism here would mean a box that never came up looking fine.
        var server = NewServer();

        Assert.Equal(ServerStatus.Unknown, server.Status);
        Assert.Null(server.LastSeenAt);
        Assert.True(server.IsActive);
    }

    [Fact]
    public void AServerWithoutANameIsRefused()
    {
        Assert.Throws<DomainException>(() => Server.Register(
            Guid.CreateVersion7(), Now, "   ", ServerHostingModel.SharedManaged, ServerEnvironment.Production));
    }

    [Fact]
    public void AHeartbeatMakesAServerHealthyAndClearsItsReason()
    {
        var server = NewServer();
        server.ApplyStatus(ServerStatus.Degraded, "Disk is 92% full.", Now);

        server.RecordHeartbeat(Now.AddMinutes(1));

        Assert.Equal(ServerStatus.Healthy, server.Status);
        Assert.Null(server.StatusReason);
        Assert.Equal(Now.AddMinutes(1), server.LastSeenAt);
    }

    [Fact]
    public void AServerThatHasNeverReportedIsNeverOverdue()
    {
        // It cannot be offline, because it was never online. Alerting on a box
        // somebody registered an hour ago and has not finished building is noise.
        var server = NewServer();

        Assert.False(server.IsOverdue(Now.AddDays(7), Interval));
    }

    [Fact]
    public void OneMissedHeartbeatIsNotAnOutage()
    {
        // A single missed heartbeat is a network hiccup, and paging somebody for
        // it is how alerts get ignored.
        var server = NewServer();
        server.RecordHeartbeat(Now);

        Assert.False(server.IsOverdue(Now.AddMinutes(1).AddSeconds(30), Interval));
        Assert.False(server.IsOverdue(Now.AddMinutes(2), Interval));
        Assert.True(server.IsOverdue(Now.AddMinutes(4), Interval));
    }

    [Fact]
    public void ApplyingTheSameStatusTwiceDoesNotChurnTheRecord()
    {
        var server = NewServer();
        server.ApplyStatus(ServerStatus.Degraded, "Disk is 92% full.", Now);
        var firstUpdate = server.UpdatedAt;

        server.ApplyStatus(ServerStatus.Degraded, "Disk is 92% full.", Now.AddMinutes(5));

        Assert.Equal(firstUpdate, server.UpdatedAt);
    }

    [Fact]
    public void ADecommissionedServerRefusesEverything()
    {
        var server = NewServer();
        server.Decommission(Now);

        Assert.Equal(ServerStatus.Offline, server.Status);
        Assert.False(server.IsActive);
        Assert.Throws<DomainException>(() => server.RecordHeartbeat(Now));
        Assert.Throws<DomainException>(() => server.UpdateDetails("x", null, null, null, Now));
        Assert.Throws<DomainException>(() => server.Decommission(Now));
    }

    // --- Agent enrolment ---------------------------------------------------

    [Fact]
    public void ANewAgentIsProvisioningAndCanEnrol()
    {
        var agent = NewAgent();

        Assert.Equal(AgentStatus.Provisioning, agent.Status);
        Assert.True(agent.CanEnrol(Now));
        Assert.Null(agent.CredentialHash);
    }

    [Fact]
    public void EnrolmentBurnsTheProvisioningToken()
    {
        // Not merely marks it used — the hash goes, so a captured token cannot
        // even be replayed against it.
        var agent = NewAgent();

        agent.CompleteEnrolment("credential-hash", "1.0.0", null, Now);

        Assert.Null(agent.ProvisioningTokenHash);
        Assert.Null(agent.ProvisioningExpiresAt);
        Assert.Equal(AgentStatus.Online, agent.Status);
        Assert.False(agent.CanEnrol(Now));
    }

    [Fact]
    public void AProvisioningTokenCannotBeUsedTwice()
    {
        // A second enrolment is either a replay or a second machine using a token
        // meant for the first. Both must be refused.
        var agent = NewAgent();
        agent.CompleteEnrolment("credential-hash", "1.0.0", null, Now);

        var exception = Assert.Throws<DomainException>(() =>
            agent.CompleteEnrolment("another-hash", "1.0.0", null, Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void AnExpiredProvisioningTokenIsRefused()
    {
        var agent = NewAgent();
        var tooLate = Now.Add(Agent.ProvisioningWindow).AddMinutes(1);

        Assert.False(agent.CanEnrol(tooLate));
        Assert.Throws<DomainException>(() => agent.CompleteEnrolment("hash", null, null, tooLate));
    }

    [Fact]
    public void AnAgentMustBelongToAServer()
    {
        Assert.Throws<DomainException>(() =>
            Agent.Provision(Guid.CreateVersion7(), Now, Guid.Empty, "hash"));
    }

    [Fact]
    public void AnAgentThatHasNotEnrolledCannotReport()
    {
        var agent = NewAgent();

        Assert.Throws<DomainException>(() => agent.RecordHeartbeat("1.0.0", null, Now));
    }

    [Fact]
    public void RevocationTakesTheCredentialWithIt()
    {
        var agent = NewAgent();
        agent.CompleteEnrolment("credential-hash", "1.0.0", null, Now);

        agent.Revoke("The machine was compromised.", Now.AddHours(1));

        Assert.Equal(AgentStatus.Revoked, agent.Status);
        Assert.Null(agent.CredentialHash);
        Assert.Equal("The machine was compromised.", agent.RevokedReason);
    }

    [Fact]
    public void ARevokedAgentIsTerminal()
    {
        var agent = NewAgent();
        agent.CompleteEnrolment("credential-hash", null, null, Now);
        agent.Revoke("compromised", Now);

        Assert.Throws<DomainException>(() => agent.RecordHeartbeat("1.0.0", null, Now));
        Assert.Throws<DomainException>(() => agent.Revoke("again", Now));

        // And it stays revoked even if the sweep runs over it.
        agent.MarkOffline(Now);
        Assert.Equal(AgentStatus.Revoked, agent.Status);
    }

    [Fact]
    public void ARevocationMustSayWhy()
    {
        var agent = NewAgent();
        agent.CompleteEnrolment("hash", null, null, Now);

        Assert.Throws<DomainException>(() => agent.Revoke("  ", Now));
    }

    [Fact]
    public void AnOfflineAgentComesBackOnItsNextHeartbeat()
    {
        var agent = NewAgent();
        agent.CompleteEnrolment("hash", null, null, Now);
        agent.MarkOffline(Now.AddMinutes(5));

        agent.RecordHeartbeat("1.1.0", null, Now.AddMinutes(6));

        Assert.Equal(AgentStatus.Online, agent.Status);
        Assert.Equal("1.1.0", agent.Version);
    }

    // --- Metrics -----------------------------------------------------------

    [Fact]
    public void AMetricComputesItsOwnPercentages()
    {
        var metric = ServerMetric.Capture(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Now,
            cpuPercent: 40, memoryUsedBytes: 512, memoryTotalBytes: 1024,
            diskUsedBytes: 90, diskTotalBytes: 100);

        Assert.Equal(50, metric.MemoryPercent);
        Assert.Equal(90, metric.DiskPercent);
    }

    [Fact]
    public void AnImpossibleCpuReadingIsClampedRatherThanLosingTheWholeSample()
    {
        // 101% is a rounding artefact from the agent's platform. Throwing the
        // sample away would lose its memory and disk figures too.
        var metric = ServerMetric.Capture(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Now, 140, 1, 2, 1, 2);

        Assert.Equal(100, metric.CpuPercent);
    }

    [Fact]
    public void AMetricWithNoTotalsReportsNoPercentages()
    {
        var metric = ServerMetric.Capture(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Now, 10, 0, 0, 0, 0);

        Assert.Null(metric.MemoryPercent);
        Assert.Null(metric.DiskPercent);
    }

    [Fact]
    public void ANegativeByteCountIsRefused()
    {
        Assert.Throws<DomainException>(() => ServerMetric.Capture(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Now, 10, -1, 2, 1, 2));
    }

    // --- Alerts ------------------------------------------------------------

    [Fact]
    public void AnAlertStartsOpenWithOneOccurrence()
    {
        var alert = Alert.Raise(
            Guid.CreateVersion7(), Now, AlertSource.Server, Guid.CreateVersion7(),
            AlertSeverity.Critical, AlertRules.ServerOffline, "web-01 has not reported for 3 minutes.");

        Assert.True(alert.IsOpen);
        Assert.Equal(1, alert.OccurrenceCount);
        Assert.Null(alert.AcknowledgedAt);
    }

    [Fact]
    public void ObservingAnAlertCountsItAndRefreshesTheMessage()
    {
        // "offline for 5 minutes" becomes "offline for 3 hours". A stale message
        // tells an operator the wrong thing with total confidence.
        var alert = Alert.Raise(
            Guid.CreateVersion7(), Now, AlertSource.Server, Guid.CreateVersion7(),
            AlertSeverity.Critical, AlertRules.ServerOffline, "offline for 3 minutes");

        alert.Observe("offline for 3 hours", Now.AddHours(3));

        Assert.Equal(2, alert.OccurrenceCount);
        Assert.Equal("offline for 3 hours", alert.Message);
        Assert.Equal(TimeSpan.FromHours(3), alert.Duration(Now.AddHours(3)));
    }

    [Fact]
    public void AcknowledgingDoesNotCloseAnAlert()
    {
        // Somebody looking at it does not make the world better.
        var alert = Alert.Raise(
            Guid.CreateVersion7(), Now, AlertSource.Server, Guid.CreateVersion7(),
            AlertSeverity.Warning, AlertRules.ServerDegraded, "Disk is 92% full.");

        alert.Acknowledge(Guid.CreateVersion7(), Now.AddMinutes(1));

        Assert.True(alert.IsOpen);
        Assert.NotNull(alert.AcknowledgedAt);
    }

    [Fact]
    public void AResolvedAlertIsNotObservedAgain()
    {
        var alert = Alert.Raise(
            Guid.CreateVersion7(), Now, AlertSource.Server, Guid.CreateVersion7(),
            AlertSeverity.Critical, AlertRules.ServerOffline, "offline");

        alert.Resolve(Now.AddMinutes(10));

        Assert.False(alert.IsOpen);
        Assert.Throws<DomainException>(() => alert.Observe("offline again", Now.AddMinutes(11)));
    }

    [Fact]
    public void ResolvingTwiceKeepsTheFirstResolutionTime()
    {
        var alert = Alert.Raise(
            Guid.CreateVersion7(), Now, AlertSource.Server, Guid.CreateVersion7(),
            AlertSeverity.Info, AlertRules.ServerDegraded, "degraded");

        alert.Resolve(Now.AddMinutes(5));
        alert.Resolve(Now.AddMinutes(9));

        Assert.Equal(Now.AddMinutes(5), alert.ResolvedAt);
    }

    [Fact]
    public void AnAlertMustNameItsRuleAndSubject()
    {
        Assert.Throws<DomainException>(() => Alert.Raise(
            Guid.CreateVersion7(), Now, AlertSource.Server, Guid.Empty,
            AlertSeverity.Info, AlertRules.ServerOffline, "x"));

        Assert.Throws<DomainException>(() => Alert.Raise(
            Guid.CreateVersion7(), Now, AlertSource.Server, Guid.CreateVersion7(),
            AlertSeverity.Info, "  ", "x"));

        Assert.Throws<DomainException>(() => Alert.Raise(
            Guid.CreateVersion7(), Now, AlertSource.Server, Guid.CreateVersion7(),
            AlertSeverity.Info, AlertRules.ServerOffline, "  "));
    }
}
