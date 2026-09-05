using AutoAdmin.Domain;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Domain.Exceptions;
using Subscriptions;

namespace AutoAdmin;

/// <summary>
/// Orchestrates a run of the Automatic Admin over the seams
/// (docs/adr/0038). Entitlement is the gate throughout: the admin generates only
/// the kinds and publishes only to the channels the customer actually bought,
/// resolved from <see cref="IEntitlementService"/> and never from anything the
/// caller sends.
/// </summary>
internal sealed class AutoAdminService : IAutoAdminService
{
    private readonly IAutoAdminSettingsRepository _settings;
    private readonly IContentJobRepository _jobs;
    private readonly IEntitlementService _entitlements;
    private readonly IFeatureRepository _features;
    private readonly IContentGenerator _generator;
    private readonly IChannelPublisherRegistry _publishers;
    private readonly IDateTimeProvider _clock;

    public AutoAdminService(
        IAutoAdminSettingsRepository settings,
        IContentJobRepository jobs,
        IEntitlementService entitlements,
        IFeatureRepository features,
        IContentGenerator generator,
        IChannelPublisherRegistry publishers,
        IDateTimeProvider clock)
    {
        _settings = settings;
        _jobs = jobs;
        _entitlements = entitlements;
        _features = features;
        _generator = generator;
        _publishers = publishers;
        _clock = clock;
    }

    public async Task<AutoAdminSettings> GetSettingsAsync(Guid customerId, CancellationToken cancellationToken) =>
        await GetOrCreateSettingsAsync(customerId, cancellationToken);

    public async Task<AutoAdminSettings> SetAutonomyAsync(Guid customerId, AutonomyMode autonomy, CancellationToken cancellationToken)
    {
        var settings = await GetOrCreateSettingsAsync(customerId, cancellationToken);
        settings.SetAutonomy(autonomy, _clock.UtcNow);
        await _settings.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<ContentJob> SubmitAsync(Guid customerId, string topic, CancellationToken cancellationToken)
    {
        var entitledParts = await ResolveEntitledPartsAsync(customerId, cancellationToken);

        var kinds = AutoAdminParts.GenerationKinds
            .Where(part => entitledParts.Contains(part.Key))
            .Select(part => part.Value)
            .OrderBy(kind => (int)kind)
            .ToArray();

        if (kinds.Length == 0)
        {
            throw DomainException.Conflict(
                "The customer holds no Automatic Admin generation capability, so there is nothing to generate.");
        }

        var settings = await GetOrCreateSettingsAsync(customerId, cancellationToken);
        var now = _clock.UtcNow;
        var job = ContentJob.Create(Guid.NewGuid(), now, customerId, topic, settings.Autonomy);

        foreach (var kind in kinds)
        {
            var content = await _generator.GenerateAsync(new GenerationBrief(customerId, topic, kind), cancellationToken);
            job.AddDraft(Guid.NewGuid(), kind, content.Body, content.GeneratorName);
        }

        // Full-auto publishes straight away; otherwise the run waits as a draft
        // for the merchant to approve (docs/adr/0038).
        if (settings.Autonomy is AutonomyMode.FullyAutomatic)
        {
            await PublishAsync(job, entitledParts, cancellationToken);
        }

        await _jobs.AddAsync(job, cancellationToken);
        await _jobs.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<ContentJob> ApproveAsync(Guid customerId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetAsync(jobId, cancellationToken);
        if (job is null || job.CustomerId != customerId)
        {
            throw new NotFoundException("Content job", jobId);
        }

        var entitledParts = await ResolveEntitledPartsAsync(customerId, cancellationToken);
        await PublishAsync(job, entitledParts, cancellationToken);
        await _jobs.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<ContentJob?> GetJobAsync(Guid customerId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetAsync(jobId, cancellationToken);
        return job is not null && job.CustomerId == customerId ? job : null;
    }

    public Task<IReadOnlyCollection<ContentJob>> ListJobsAsync(Guid customerId, CancellationToken cancellationToken) =>
        _jobs.ListForCustomerAsync(customerId, cancellationToken);

    /// <summary>
    /// Publishes a draft job's content to every channel the customer is entitled
    /// to. A channel with no publisher configured is recorded as a failed
    /// publication rather than skipped, so a report never hides that a connected
    /// channel went nowhere.
    /// </summary>
    private async Task PublishAsync(ContentJob job, IReadOnlySet<string> entitledParts, CancellationToken cancellationToken)
    {
        var channelKeys = AutoAdminParts.Channels
            .Where(channel => entitledParts.Contains(channel.Key))
            .Select(channel => channel.Value)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        var content = job.Drafts
            .Select(draft => new GeneratedContent(draft.Kind, draft.Body, draft.GeneratorName))
            .ToArray();

        var now = _clock.UtcNow;
        var records = new List<PublicationRecord>(channelKeys.Length);

        foreach (var channelKey in channelKeys)
        {
            if (_publishers.TryResolve(channelKey, out var publisher))
            {
                var outcome = await publisher.PublishAsync(
                    new PublishRequest(channelKey, job.Topic, content), cancellationToken);
                records.Add(new PublicationRecord(
                    channelKey, outcome.Succeeded, outcome.Detail, outcome.ExternalReference, publisher.Name));
            }
            else
            {
                records.Add(new PublicationRecord(
                    channelKey, Succeeded: false, $"No publisher is configured for channel '{channelKey}'.", null, "none"));
            }
        }

        job.RecordPublications(records, Guid.NewGuid, now);
    }

    private async Task<AutoAdminSettings> GetOrCreateSettingsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetForCustomerAsync(customerId, cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = AutoAdminSettings.CreateDefault(Guid.NewGuid(), _clock.UtcNow, customerId);
        await _settings.AddAsync(settings, cancellationToken);
        await _settings.SaveChangesAsync(cancellationToken);
        return settings;
    }

    /// <summary>
    /// The set of Automatic Admin part slugs the customer holds an active
    /// entitlement for. This is the gate: everything the admin does is filtered
    /// through it.
    /// </summary>
    private async Task<IReadOnlySet<string>> ResolveEntitledPartsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var entitlements = await _entitlements.ResolveForCustomerAsync(customerId, includeInactive: false, cancellationToken);
        var ids = entitlements.Where(e => e.IsActive).Select(e => e.FeatureId).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var features = await _features.GetManyAsync(ids, cancellationToken);
        return features
            .Where(feature => AutoAdminParts.IsPart(feature.Slug))
            .Select(feature => feature.Slug)
            .ToHashSet(StringComparer.Ordinal);
    }
}
