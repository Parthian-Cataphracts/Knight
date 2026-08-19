using Knight.Domain.Exceptions;
using Observability.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The fingerprinting rules of [`adr/0013`](../../../../docs/adr/0013-error-grouping-strategy.md).
///
/// These tests are the specification of what grouping is allowed to throw away.
/// Every one of them is a case that, got wrong, either fragments one problem into
/// hundreds of groups or merges unrelated failures into one — and both make the
/// errors screen useless in a way that is hard to notice until an incident.
/// </summary>
public sealed class ErrorFingerprintTests
{
    private static readonly Guid Store = Guid.NewGuid();

    private const string Trace = """
        File "/srv/app/apps/orders/views.py", line 142, in create
            order = Order.objects.create(**payload)
        File "/usr/lib/python3.12/site-packages/django/db/models/query.py", line 671, in create
            obj.save(force_insert=True, using=self.db)
        """;

    [Fact]
    public void LineNumbersDoNotChangeTheFingerprint()
    {
        // The single most important property. Somebody adds an import at the top
        // of views.py, every line number shifts, and the problem must stay the
        // same problem rather than being reborn as a new group.
        var before = Compute(stackTrace: Trace);
        var after = Compute(stackTrace: Trace.Replace("line 142", "line 187"));

        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void DeploymentPathsDoNotChangeTheFingerprint()
    {
        var before = Compute(stackTrace: Trace);
        var after = Compute(stackTrace: Trace.Replace("/srv/app/", "/srv/app-2026-08-19/"));

        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void VendorFramesAreIgnored()
    {
        // Only the application frame survives normalisation, so replacing the
        // Django frame with a different Django frame changes nothing.
        var normalised = ErrorFingerprint.NormaliseStackTop(Trace);

        Assert.Equal("apps/orders/views.py:create", normalised);
        Assert.DoesNotContain("django", normalised, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConcreteIdsInTheRouteDoNotChangeTheFingerprint()
    {
        var first = Compute(endpoint: "/api/orders/5182/items");
        var second = Compute(endpoint: "/api/orders/5183/items");

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal("/api/orders/{id}/items", first.EndpointTemplate);
    }

    [Fact]
    public void UuidsInTheRouteAreTreatedAsIds()
    {
        var template = ErrorFingerprint.NormaliseEndpoint($"/api/stores/{Guid.NewGuid()}/health");

        Assert.Equal("/api/stores/{id}/health", template);
    }

    [Fact]
    public void QueryStringsAreDiscarded()
    {
        Assert.Equal(
            "/api/orders",
            ErrorFingerprint.NormaliseEndpoint("/api/orders?token=secret&page=2"));
    }

    [Fact]
    public void SegmentsThatMerelyContainDigitsAreNotIdentifiers()
    {
        // "v1" is a name. Replacing it would merge /api/v1/orders with
        // /api/v2/orders, which are genuinely different routes.
        Assert.Equal("/api/v1/orders", ErrorFingerprint.NormaliseEndpoint("/api/v1/orders"));
    }

    [Fact]
    public void DifferentRoutesAreDifferentProblems()
    {
        Assert.NotEqual(
            Compute(endpoint: "/api/orders").Fingerprint,
            Compute(endpoint: "/api/invoices").Fingerprint);
    }

    [Fact]
    public void DifferentStoresAreDifferentProblems()
    {
        var other = ErrorFingerprint.Compute(
            Guid.NewGuid(), "Production", "IntegrityError", "boom", "/api/orders", Trace);

        Assert.NotEqual(Compute().Fingerprint, other.Fingerprint);
    }

    [Fact]
    public void DifferentEnvironmentsAreDifferentProblems()
    {
        // Staging failing is not production failing, however identical the
        // traceback. Collapsing them would let a staging spike mask a production
        // one on the same row.
        Assert.NotEqual(
            Compute(environment: "Production").Fingerprint,
            Compute(environment: "Staging").Fingerprint);
    }

    [Fact]
    public void TheStoreVersionIsNotPartOfTheIdentity()
    {
        // Explicitly: a problem must persist across a deployment. The version is
        // recorded per event and shown as "first seen in", never fingerprinted.
        var one = ErrorFingerprint.Compute(Store, "Production", "IntegrityError", "boom", "/api/orders", Trace);
        var two = ErrorFingerprint.Compute(Store, "Production", "IntegrityError", "boom", "/api/orders", Trace);

        Assert.Equal(one.Fingerprint, two.Fingerprint);
    }

    [Fact]
    public void VariablePartsOfTheMessageAreNormalisedOutOfTheTitle()
    {
        var title = ErrorFingerprint.Title(
            "IntegrityError",
            "duplicate key value violates unique constraint \"orders_reference_key\" (id)=(5182)");

        Assert.DoesNotContain("5182", title, StringComparison.Ordinal);
        Assert.Contains("{n}", title, StringComparison.Ordinal);
        Assert.Contains("{value}", title, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingStackTraceStillProducesAStableFingerprint()
    {
        // Not every error arrives with a traceback. It must still group by type
        // and route rather than falling back to something unique per event.
        var first = Compute(stackTrace: null);
        var second = Compute(stackTrace: null);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(string.Empty, first.NormalisedStackTop);
    }

    [Fact]
    public void TheAlgorithmVersionIsRecorded()
    {
        Assert.Equal(ErrorFingerprint.Version, Compute().FingerprintVersion);
    }

    private static ErrorFingerprintResult Compute(
        string environment = "Production",
        string exceptionType = "IntegrityError",
        string message = "boom",
        string? endpoint = "/api/orders",
        string? stackTrace = Trace) =>
        ErrorFingerprint.Compute(Store, environment, exceptionType, message, endpoint, stackTrace);
}

/// <summary>
/// The group lifecycle: counting, acknowledging, resolving, and the regression
/// rule that makes "resolved" mean something.
/// </summary>
public sealed class ErrorGroupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordingCountsWithoutAddingRows()
    {
        var group = Open();

        group.Record(Now, Now, "1.0.0", sampled: true);
        group.Record(Now.AddMinutes(1), Now.AddMinutes(1), "1.0.0", sampled: true);
        group.Record(Now.AddMinutes(2), Now.AddMinutes(2), "1.0.0", sampled: false);

        Assert.Equal(3, group.OccurrenceCount);

        // Only the sampled ones are kept in full; the third is a counter.
        Assert.Equal(2, group.SampleCount);
    }

    [Fact]
    public void AResolvedGroupThatRecursIsReopenedAsARegression()
    {
        var group = Open();
        var user = Guid.NewGuid();

        group.Record(Now, Now, "1.0.0", sampled: true);
        group.Resolve(user, Now.AddHours(1), "1.1.0");

        var regressed = group.Record(Now.AddHours(2), Now.AddHours(2), "1.1.0", sampled: true);

        Assert.True(regressed);
        Assert.True(group.IsRegression);
        Assert.Equal(ErrorGroupStatus.New, group.Status);
        Assert.Null(group.ResolvedAt);
    }

    [Fact]
    public void AnOrdinaryRecurrenceIsNotARegression()
    {
        var group = Open();

        Assert.False(group.Record(Now, Now, "1.0.0", sampled: true));
        Assert.False(group.Record(Now.AddMinutes(1), Now.AddMinutes(1), "1.0.0", sampled: true));
        Assert.False(group.IsRegression);
    }

    [Fact]
    public void AnIgnoredGroupKeepsCountingButNeverReopens()
    {
        // Being told again about something you have already dismissed is how an
        // alert channel becomes unread.
        var group = Open();

        group.Ignore(Guid.NewGuid(), Now);

        var regressed = group.Record(Now.AddHours(1), Now.AddHours(1), "1.0.0", sampled: false);

        Assert.False(regressed);
        Assert.Equal(ErrorGroupStatus.Ignored, group.Status);
        Assert.Equal(1, group.OccurrenceCount);
        Assert.False(group.IsAlertable);
    }

    [Fact]
    public void AnAcknowledgedGroupIsNotAlertable()
    {
        var group = Open();

        group.Acknowledge(Guid.NewGuid(), Now);

        // Somebody is already holding this one; a spike alert would be that
        // decision being overruled by a counter.
        Assert.False(group.IsAlertable);
    }

    [Fact]
    public void ANewGroupIsAlertable()
    {
        Assert.True(Open().IsAlertable);
    }

    [Fact]
    public void LateArrivingEventsDoNotMakeAGroupLookYounger()
    {
        // A store that was offline flushes its backlog. The first occurrence
        // moves earlier, and the last seen time must not move backwards with it.
        var group = Open();

        group.Record(Now, Now, "1.0.0", sampled: true);
        group.Record(Now.AddHours(-3), Now, "1.0.0", sampled: true);

        Assert.Equal(Now.AddHours(-3), group.FirstSeenAt);
        Assert.Equal(Now, group.LastSeenAt);
    }

    [Fact]
    public void ResolvingAnAcknowledgedGroupIsAllowed()
    {
        var group = Open();
        var user = Guid.NewGuid();

        group.Acknowledge(user, Now);
        group.Resolve(user, Now.AddMinutes(5), "1.1.0");

        Assert.Equal(ErrorGroupStatus.Resolved, group.Status);
        Assert.Equal("1.1.0", group.ResolvedInVersion);
    }

    [Fact]
    public void AcknowledgingAResolvedGroupIsRefused()
    {
        var group = Open();
        var user = Guid.NewGuid();

        group.Resolve(user, Now, null);

        Assert.Throws<DomainException>(() => group.Acknowledge(user, Now.AddMinutes(1)));
    }

    [Fact]
    public void AGroupMustCarryAFingerprint()
    {
        Assert.Throws<DomainException>(() => ErrorGroup.Open(
            Guid.NewGuid(),
            Now,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ErrorFingerprintResult(string.Empty, 1, "t", "/", string.Empty),
            "Production",
            "IntegrityError",
            "1.0.0"));
    }

    [Fact]
    public void AGroupMustBelongToAStore()
    {
        Assert.Throws<DomainException>(() => ErrorGroup.Open(
            Guid.NewGuid(),
            Now,
            Guid.NewGuid(),
            Guid.Empty,
            Print(),
            "Production",
            "IntegrityError",
            "1.0.0"));
    }

    private static ErrorGroup Open() => ErrorGroup.Open(
        Guid.NewGuid(),
        Now,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Print(),
        "Production",
        "IntegrityError",
        "1.0.0");

    private static ErrorFingerprintResult Print() =>
        new("abc123", ErrorFingerprint.Version, "IntegrityError: boom", "/api/orders", "views.py:create");
}
