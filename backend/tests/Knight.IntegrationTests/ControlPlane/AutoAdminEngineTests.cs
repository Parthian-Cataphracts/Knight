using AutoAdmin;
using AutoAdmin.Domain;
using FeatureRegistry.Domain;
using Knight.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Subscriptions;
using Xunit;

namespace Knight.IntegrationTests.ControlPlane;

/// <summary>
/// The Automatic Admin engine exercised against a real database and the real DI
/// graph (docs/adr/0038): a topic becomes content for the entitled parts, waits
/// for approval by default, and — once approved — publishes to the entitled
/// channels through the simulated adapter, with the whole run persisted and read
/// back. This is the delivery drill's equivalent for the engine: the seams and
/// the orchestrator run end to end with no AI key and no channel account.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AutoAdminEngineTests
{
    private readonly PostgresApiFixture _fixture;

    public AutoAdminEngineTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task EntitleAsync(Guid customerId, params string[] slugs)
    {
        // Granted in platform scope through the real entitlement service — the same
        // manual grant the dashboard uses for a pilot.
        await _fixture.WithControlPlaneScopeAsync(async (_, sp) =>
        {
            var features = sp.GetRequiredService<IFeatureRepository>();
            var entitlements = sp.GetRequiredService<IEntitlementService>();

            foreach (var slug in slugs)
            {
                var feature = await features.GetBySlugAsync(slug, CancellationToken.None);
                Assert.NotNull(feature); // the catalogue seed publishes every auto-admin part
                await entitlements.GrantAsync(customerId, feature!.Id, customerId, expiresAt: null, CancellationToken.None);
            }
        });
    }

    private Task<T> AsCustomerAsync<T>(Guid customerId, Func<IAutoAdminService, Task<T>> action) =>
        _fixture.WithControlPlaneScopeAsync(
            (_, sp) => action(sp.GetRequiredService<IAutoAdminService>()),
            customerId);

    [Fact]
    public async Task DraftThenApprovePublishesToTheEntitledChannelAndPersists()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var customerId = await _fixture.SeedCustomerAsync();
        await EntitleAsync(customerId, "auto-admin-image", "auto-admin-caption", "auto-admin-telegram");

        // Default autonomy: the run is generated and waits for approval.
        var draft = await AsCustomerAsync(customerId, service => service.SubmitAsync(customerId, "Yalda sale", CancellationToken.None));

        Assert.Equal(ContentJobStatus.Draft, draft.Status);
        Assert.Equal(2, draft.Drafts.Count); // image + caption; the channel is not a kind
        Assert.Empty(draft.Publications);

        // Approving — in a fresh scope, as a separate request would — publishes to
        // the one entitled channel.
        var published = await AsCustomerAsync(customerId, service => service.ApproveAsync(customerId, draft.Id, CancellationToken.None));

        Assert.Equal(ContentJobStatus.Published, published.Status);
        Assert.Single(published.Publications);
        Assert.Equal("telegram", published.Publications.First().ChannelKey);
        Assert.True(published.Publications.First().Succeeded);

        // Read back from the database in a fresh scope: the run, its drafts and its
        // publication all persisted.
        var reloaded = await AsCustomerAsync(customerId, service => service.GetJobAsync(customerId, draft.Id, CancellationToken.None));

        Assert.NotNull(reloaded);
        Assert.Equal(ContentJobStatus.Published, reloaded!.Status);
        Assert.Equal(2, reloaded.Drafts.Count);
        Assert.Single(reloaded.Publications);
    }

    [Fact]
    public async Task FullyAutomaticPublishesOnSubmit()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var customerId = await _fixture.SeedCustomerAsync();
        await EntitleAsync(customerId, "auto-admin-caption", "auto-admin-telegram", "auto-admin-instagram");

        await AsCustomerAsync(customerId, async service =>
        {
            await service.SetAutonomyAsync(customerId, AutonomyMode.FullyAutomatic, CancellationToken.None);
            return true;
        });

        var job = await AsCustomerAsync(customerId, service => service.SubmitAsync(customerId, "Flash sale", CancellationToken.None));

        Assert.Equal(ContentJobStatus.Published, job.Status);
        Assert.Equal(2, job.Publications.Count); // telegram + instagram
        Assert.True(job.Publications.All(p => p.Succeeded));
    }

    [Fact]
    public async Task OnlyBoughtPartsAreActedOn()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var customerId = await _fixture.SeedCustomerAsync();
        // A generation part and a channel, but not the caption or the other channels.
        await EntitleAsync(customerId, "auto-admin-image", "auto-admin-telegram");

        await AsCustomerAsync(customerId, async service =>
        {
            await service.SetAutonomyAsync(customerId, AutonomyMode.FullyAutomatic, CancellationToken.None);
            return true;
        });

        var job = await AsCustomerAsync(customerId, service => service.SubmitAsync(customerId, "New arrivals", CancellationToken.None));

        Assert.Single(job.Drafts); // only the image was entitled
        Assert.Equal(ContentKind.Image, job.Drafts.First().Kind);
        Assert.Single(job.Publications); // only telegram
        Assert.Equal("telegram", job.Publications.First().ChannelKey);
    }
}
