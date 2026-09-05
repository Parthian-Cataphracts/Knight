using AutoAdmin;
using AutoAdmin.Adapters;
using AutoAdmin.Domain;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Domain.Exceptions;
using NSubstitute;
using Subscriptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The Automatic Admin orchestrator (docs/adr/0038): entitlement gates what it
/// generates and where it publishes, autonomy decides draft-vs-publish, and a run
/// is published exactly once.
/// </summary>
public sealed class AutoAdminServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Customer = Guid.NewGuid();

    private readonly IEntitlementService _entitlements = Substitute.For<IEntitlementService>();
    private readonly IFeatureRepository _features = Substitute.For<IFeatureRepository>();
    private readonly IDateTimeProvider _clock = Substitute.For<IDateTimeProvider>();
    private readonly FakeSettingsRepository _settings = new();
    private readonly FakeJobRepository _jobs = new();
    private readonly IAutoAdminService _service;

    public AutoAdminServiceTests()
    {
        _clock.UtcNow.Returns(Now);

        var publishers = AutoAdminParts.Channels.Values
            .Distinct(StringComparer.Ordinal)
            .Select(key => (IChannelPublisher)new SimulatedChannelPublisher(key))
            .ToArray();

        _service = new AutoAdminService(
            _settings,
            _jobs,
            _entitlements,
            _features,
            new SimulatedContentGenerator(),
            new ChannelPublisherRegistry(publishers),
            _clock);
    }

    private void Entitle(Guid customerId, params string[] slugs)
    {
        var features = slugs
            .Select(slug => Feature.Create(Guid.NewGuid(), Now, slug, slug, "Automation"))
            .ToArray();

        var views = features
            .Select(f => new EntitlementView(f.Id, "Plan", Now, null, IsActive: true))
            .ToArray();

        _entitlements
            .ResolveForCustomerAsync(customerId, false, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<EntitlementView>)views);

        _features
            .GetManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<Feature>)features);
    }

    [Fact]
    public async Task AutonomyDefaultsToApprovalRequired()
    {
        var settings = await _service.GetSettingsAsync(Customer, CancellationToken.None);

        Assert.Equal(AutonomyMode.ApprovalRequired, settings.Autonomy);
    }

    [Fact]
    public async Task SubmitGeneratesForEntitledKindsAndWaitsForApprovalByDefault()
    {
        Entitle(Customer, "auto-admin-image", "auto-admin-caption", "auto-admin-telegram");

        var job = await _service.SubmitAsync(Customer, "Yalda sale", CancellationToken.None);

        Assert.Equal(ContentJobStatus.Draft, job.Status);
        Assert.True(job.AwaitingApproval);
        Assert.Equal(2, job.Drafts.Count); // image + caption; the channel is not a kind
        Assert.Empty(job.Publications); // nothing published until approved
    }

    [Fact]
    public async Task OnlyEntitledKindsAreGenerated()
    {
        Entitle(Customer, "auto-admin-image"); // caption not entitled

        var job = await _service.SubmitAsync(Customer, "New arrivals", CancellationToken.None);

        Assert.Single(job.Drafts);
        Assert.Equal(ContentKind.Image, job.Drafts.First().Kind);
    }

    [Fact]
    public async Task ApprovePublishesToTheEntitledChannels()
    {
        Entitle(Customer, "auto-admin-image", "auto-admin-telegram"); // instagram not entitled

        var draft = await _service.SubmitAsync(Customer, "Sale", CancellationToken.None);
        var published = await _service.ApproveAsync(Customer, draft.Id, CancellationToken.None);

        Assert.Equal(ContentJobStatus.Published, published.Status);
        Assert.Single(published.Publications);
        Assert.Equal("telegram", published.Publications.First().ChannelKey);
        Assert.True(published.Publications.All(p => p.Succeeded));
        Assert.False(published.HasPublicationErrors);
    }

    [Fact]
    public async Task FullyAutomaticPublishesOnSubmit()
    {
        await _service.SetAutonomyAsync(Customer, AutonomyMode.FullyAutomatic, CancellationToken.None);
        Entitle(Customer, "auto-admin-caption", "auto-admin-telegram", "auto-admin-instagram");

        var job = await _service.SubmitAsync(Customer, "Flash sale", CancellationToken.None);

        Assert.Equal(ContentJobStatus.Published, job.Status);
        Assert.Equal(2, job.Publications.Count); // telegram + instagram
    }

    [Fact]
    public async Task GenerationWithNoEntitledChannelFinishesWithNothingPublished()
    {
        await _service.SetAutonomyAsync(Customer, AutonomyMode.FullyAutomatic, CancellationToken.None);
        Entitle(Customer, "auto-admin-image"); // a kind, but no channel

        var job = await _service.SubmitAsync(Customer, "Teaser", CancellationToken.None);

        // Content was generated and there was nowhere entitled to send it — a
        // finished run, not a failure.
        Assert.Equal(ContentJobStatus.Published, job.Status);
        Assert.Empty(job.Publications);
    }

    [Fact]
    public async Task SubmitIsRefusedWhenNoGenerationPartIsEntitled()
    {
        Entitle(Customer, "auto-admin-telegram"); // a channel, but nothing to generate

        await Assert.ThrowsAsync<DomainException>(
            () => _service.SubmitAsync(Customer, "x", CancellationToken.None));
    }

    [Fact]
    public async Task ApprovingAnAlreadyPublishedRunIsRefused()
    {
        Entitle(Customer, "auto-admin-image", "auto-admin-telegram");
        var draft = await _service.SubmitAsync(Customer, "Sale", CancellationToken.None);
        await _service.ApproveAsync(Customer, draft.Id, CancellationToken.None);

        await Assert.ThrowsAsync<DomainException>(
            () => _service.ApproveAsync(Customer, draft.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ApprovingAnotherCustomersRunIsNotFound()
    {
        Entitle(Customer, "auto-admin-image", "auto-admin-telegram");
        var draft = await _service.SubmitAsync(Customer, "Sale", CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(
            () => _service.ApproveAsync(Guid.NewGuid(), draft.Id, CancellationToken.None));
    }

    private sealed class FakeSettingsRepository : IAutoAdminSettingsRepository
    {
        private readonly Dictionary<Guid, AutoAdminSettings> _byCustomer = [];

        public Task<AutoAdminSettings?> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
            Task.FromResult(_byCustomer.TryGetValue(customerId, out var settings) ? settings : null);

        public Task AddAsync(AutoAdminSettings settings, CancellationToken cancellationToken)
        {
            _byCustomer[settings.CustomerId] = settings;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeJobRepository : IContentJobRepository
    {
        private readonly Dictionary<Guid, ContentJob> _jobs = [];

        public Task<ContentJob?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);

        public Task AddAsync(ContentJob job, CancellationToken cancellationToken)
        {
            _jobs[job.Id] = job;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<ContentJob>> ListForCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
            Task.FromResult((IReadOnlyCollection<ContentJob>)_jobs.Values.Where(j => j.CustomerId == customerId).ToArray());

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
