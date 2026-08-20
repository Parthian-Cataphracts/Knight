using System.Net;
using Ingestion;
using Ingestion.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Infrastructure.Caching;
using Knight.Infrastructure.ControlPlane.Integration;
using Knight.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Stores.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The rules that protect KNIGHT from what a store sends it: batch caps, the
/// environment binding, idempotency, and the entitlement gate on the paid
/// ingestion surface.
/// </summary>
public sealed class IngestionServiceTests
{
    private static readonly IngestingStore Store = new(Guid.NewGuid(), Guid.NewGuid(), "Production");

    private readonly IIngestionRepository _repository = Substitute.For<IIngestionRepository>();
    private readonly ICustomerEntitlementReader _entitlements = Substitute.For<ICustomerEntitlementReader>();
    private readonly IReplayGuard _replay = new InProcessReplayGuard();

    // Grouping is a consequence of accepting telemetry, never a condition of it,
    // so these tests substitute it out entirely: what they assert is that a batch
    // is accepted or refused for the right reason, and that must stay true
    // whatever grouping does — including throwing.
    private readonly IErrorGrouping _grouping = Substitute.For<IErrorGrouping>();

    private IIngestionService CreateService(IngestionOptions? options = null)
    {
        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        return new IngestionService(
            _repository,
            _entitlements,
            _replay,
            _grouping,
            new NullKnightMetrics(),
            clock,
            NullLogger<IngestionService>.Instance,
            Options.Create(options ?? new IngestionOptions()));
    }

    private static ErrorEventInput Error(string message = "boom") =>
        new(DateTimeOffset.UtcNow, "RuntimeError", message, null, null, null, null, null, null, null);

    [Fact]
    public async Task Errors_FromTheRegisteredEnvironment_AreAccepted()
    {
        var receipt = await CreateService().IngestErrorsAsync(Store, "Production", "1.0.0", [Error()], null, default);

        Assert.Equal(1, receipt.Accepted);
        Assert.Equal(0, receipt.Rejected);
        await _repository.Received(1).AddErrorsAsync(Arg.Is<IReadOnlyCollection<StoreErrorEvent>>(e => e.Count == 1), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The environment in the payload and the one the token was minted for can
    /// only diverge if a store is misconfigured or a token is being used by
    /// something else. Both are worth refusing loudly.
    /// </summary>
    [Fact]
    public async Task Errors_FromAnotherEnvironment_AreRefused()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            CreateService().IngestErrorsAsync(Store, "Staging", "1.0.0", [Error()], null, default));

        await _repository.DidNotReceive().AddErrorsAsync(Arg.Any<IReadOnlyCollection<StoreErrorEvent>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ABatchOverTheCap_IsRefusedRatherThanTruncated()
    {
        var options = new IngestionOptions { MaxErrorsPerBatch = 2 };

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            CreateService(options).IngestErrorsAsync(Store, "Production", "1.0.0", [Error(), Error(), Error()], null, default));

        // Truncating would make the store believe it reported something it did not.
        Assert.Contains("at most 2", exception.Errors["events"][0]);
        await _repository.DidNotReceive().AddErrorsAsync(Arg.Any<IReadOnlyCollection<StoreErrorEvent>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnEmptyBatch_IsRefused()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            CreateService().IngestErrorsAsync(Store, "Production", "1.0.0", [], null, default));
    }

    /// <summary>
    /// One malformed event must not cost a store the rest of the batch: the bad
    /// one is counted and described, the others are written.
    /// </summary>
    [Fact]
    public async Task AMalformedEvent_DoesNotLoseTheRestOfTheBatch()
    {
        var malformed = new ErrorEventInput(DateTimeOffset.UtcNow, null, "no type", null, null, null, null, null, null, null);

        var receipt = await CreateService().IngestErrorsAsync(Store, "Production", "1.0.0", [Error(), malformed], null, default);

        Assert.Equal(1, receipt.Accepted);
        Assert.Equal(1, receipt.Rejected);
        Assert.Single(receipt.Errors);
    }

    [Fact]
    public async Task ABatchReplayedUnderTheSameKey_IsNotWrittenTwice()
    {
        var service = CreateService();

        var first = await service.IngestErrorsAsync(Store, "Production", "1.0.0", [Error()], "batch-1", default);
        var second = await service.IngestErrorsAsync(Store, "Production", "1.0.0", [Error()], "batch-1", default);

        Assert.Equal(1, first.Accepted);
        Assert.True(second.Duplicate);
        Assert.Equal(0, second.Accepted);
        await _repository.Received(1).AddErrorsAsync(Arg.Any<IReadOnlyCollection<StoreErrorEvent>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An idempotency key belongs to one store; two stores may use the same one.</summary>
    [Fact]
    public async Task AnIdempotencyKey_IsScopedToOneStore()
    {
        var service = CreateService();
        var other = new IngestingStore(Guid.NewGuid(), Guid.NewGuid(), "Production");

        await service.IngestErrorsAsync(Store, "Production", "1.0.0", [Error()], "batch-1", default);
        var second = await service.IngestErrorsAsync(other, "Production", "1.0.0", [Error()], "batch-1", default);

        Assert.False(second.Duplicate);
        Assert.Equal(1, second.Accepted);
    }

    [Fact]
    public async Task Logs_WithoutTheEntitlement_AreRefused()
    {
        _entitlements.IsEntitledAsync(Store.CustomerId, IngestionService.LogShippingFeatureSlug, Arg.Any<CancellationToken>())
            .Returns(false);

        var entry = new LogEntryInput(DateTimeOffset.UtcNow, "INFO", "web", "hello", null, null, null, null);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            CreateService().IngestLogsAsync(Store, "Production", "1.0.0", [entry], null, default));

        await _repository.DidNotReceive().AddLogsAsync(Arg.Any<IReadOnlyCollection<StoreLogEntry>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Logs_WithTheEntitlement_AreAccepted()
    {
        _entitlements.IsEntitledAsync(Store.CustomerId, IngestionService.LogShippingFeatureSlug, Arg.Any<CancellationToken>())
            .Returns(true);

        var entry = new LogEntryInput(DateTimeOffset.UtcNow, "INFO", "web", "hello", null, null, null, null);

        var receipt = await CreateService().IngestLogsAsync(Store, "Production", "1.0.0", [entry], null, default);

        Assert.Equal(1, receipt.Accepted);
    }

    /// <summary>
    /// The entitlement is checked before the idempotency claim, so a batch
    /// refused today is not swallowed as a duplicate after the capability is
    /// bought tomorrow.
    /// </summary>
    [Fact]
    public async Task ARefusedLogBatch_CanBeRetriedOnceTheEntitlementExists()
    {
        var service = CreateService();
        var entry = new LogEntryInput(DateTimeOffset.UtcNow, "INFO", "web", "hello", null, null, null, null);

        _entitlements.IsEntitledAsync(Store.CustomerId, IngestionService.LogShippingFeatureSlug, Arg.Any<CancellationToken>())
            .Returns(false);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.IngestLogsAsync(Store, "Production", "1.0.0", [entry], "batch-1", default));

        _entitlements.IsEntitledAsync(Store.CustomerId, IngestionService.LogShippingFeatureSlug, Arg.Any<CancellationToken>())
            .Returns(true);

        var receipt = await service.IngestLogsAsync(Store, "Production", "1.0.0", [entry], "batch-1", default);

        Assert.False(receipt.Duplicate);
        Assert.Equal(1, receipt.Accepted);
    }
}

/// <summary>
/// The egress rule for outbound calls to stores. A store's domain is
/// operator-supplied and resolved by DNS nobody here controls, so these are the
/// addresses KNIGHT must refuse to connect to whatever the name says
/// (docs/security-threat-model.md, SSRF).
/// </summary>
public sealed class OutboundAddressPolicyTests
{
    private static IOutboundAddressPolicy Policy(bool allowPrivate = false) =>
        new OutboundAddressPolicy(Options.Create(new StoreProbeOptions { AllowPrivateNetworks = allowPrivate }));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.9")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public void PrivateAndLoopbackAddresses_AreRefused(string address)
    {
        Assert.NotNull(Policy().Refuse(IPAddress.Parse(address)));
    }

    /// <summary>
    /// Never allowed, whatever the configuration says: this is where cloud
    /// metadata services live, and nothing about a store is reachable there.
    /// </summary>
    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    public void LinkLocalAddresses_AreRefusedEvenWhenPrivateNetworksAreAllowed(string address)
    {
        Assert.NotNull(Policy(allowPrivate: true).Refuse(IPAddress.Parse(address)));
    }

    /// <summary>An IPv4 address wearing an IPv6 costume is still that IPv4 address.</summary>
    [Fact]
    public void AnIPv4MappedLoopback_IsRefused()
    {
        Assert.NotNull(Policy().Refuse(IPAddress.Parse("::ffff:127.0.0.1")));
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    public void PublicAddresses_AreAllowed(string address)
    {
        Assert.Null(Policy().Refuse(IPAddress.Parse(address)));
    }

    /// <summary>
    /// Local development genuinely runs the reference store on loopback, which is
    /// the one case the switch exists for.
    /// </summary>
    [Fact]
    public void Loopback_IsAllowedWhenPrivateNetworksAre()
    {
        Assert.Null(Policy(allowPrivate: true).Refuse(IPAddress.Parse("127.0.0.1")));
    }
}

/// <summary>
/// Replay protection. The in-process implementation is what a single-node
/// development host runs on; production uses Redis, and the host refuses to
/// start on the fallback outside Development.
/// </summary>
public sealed class InProcessReplayGuardTests
{
    [Fact]
    public async Task AValue_IsConsumedOnlyOnce()
    {
        var guard = new InProcessReplayGuard();

        Assert.True(await guard.TryConsumeAsync("handshake", "abc", TimeSpan.FromMinutes(5), default));
        Assert.False(await guard.TryConsumeAsync("handshake", "abc", TimeSpan.FromMinutes(5), default));
    }

    [Fact]
    public async Task TheSameValue_InTwoScopes_IsTwoValues()
    {
        var guard = new InProcessReplayGuard();

        Assert.True(await guard.TryConsumeAsync("handshake", "abc", TimeSpan.FromMinutes(5), default));
        Assert.True(await guard.TryConsumeAsync("ingest", "abc", TimeSpan.FromMinutes(5), default));
    }

    [Fact]
    public async Task AnExpiredValue_IsClaimableAgain()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var guard = new InProcessReplayGuard(time);

        Assert.True(await guard.TryConsumeAsync("handshake", "abc", TimeSpan.FromMinutes(5), default));

        time.Advance(TimeSpan.FromMinutes(6));

        Assert.True(await guard.TryConsumeAsync("handshake", "abc", TimeSpan.FromMinutes(5), default));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>
/// One deployment is one row, however KNIGHT came to hear about it.
/// </summary>
public sealed class StoreDeploymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADetectedDeployment_CanBeConfirmedByTheStore()
    {
        var deployment = StoreDeployment.Detected(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "1.1.0", "1.0.0", Now);

        deployment.Confirm(StoreDeploymentStatus.Succeeded, Now.AddSeconds(-2), "Deployed by CI.");

        Assert.Equal(StoreDeploymentStatus.Succeeded, deployment.Status);
        Assert.Equal(StoreDeploymentSource.StoreReported, deployment.Source);
        Assert.Equal("Deployed by CI.", deployment.Notes);
    }

    [Fact]
    public void AConfirmedDeployment_CannotBeConfirmedTwice()
    {
        var deployment = StoreDeployment.Detected(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "1.1.0", "1.0.0", Now);
        deployment.Confirm(StoreDeploymentStatus.Succeeded, Now, null);

        Assert.Throws<Knight.Domain.Exceptions.DomainException>(() =>
            deployment.Confirm(StoreDeploymentStatus.Failed, Now, null));
    }

    [Fact]
    public void AReportedDeployment_MustSayHowItWent()
    {
        Assert.Throws<Knight.Domain.Exceptions.DomainException>(() =>
            StoreDeployment.Reported(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "1.1.0",
                "1.0.0",
                Now,
                Now,
                StoreDeploymentStatus.Detected,
                null));
    }
}
