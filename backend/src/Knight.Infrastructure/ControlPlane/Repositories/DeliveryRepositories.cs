using FeatureDelivery.Domain;
using FeatureRegistry.Domain;
using Knight.Domain.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Persistence for the registry's deployable half and for delivery.
///
/// The registry repositories deliberately do not filter by customer: the
/// catalogue is platform-wide, and every store resolves against the same one.
/// The delivery repositories say nothing about customers either, but for the
/// opposite reason — their entities are customer-owned, so the context's global
/// filter has already narrowed them, and repeating the condition here would only
/// create somewhere for the two to disagree (docs/authorization.md §3).
/// </summary>
internal sealed class FeatureVersionRepository : IFeatureVersionRepository
{
    private readonly ControlPlaneDbContext _context;

    public FeatureVersionRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<FeatureVersion?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.FeatureVersions
            .Include(version => version.Dependencies)
            .FirstOrDefaultAsync(version => version.Id == id, cancellationToken);

    public Task<FeatureVersion?> FindAsync(Guid featureId, string version, CancellationToken cancellationToken) =>
        _context.FeatureVersions
            .Include(item => item.Dependencies)
            .FirstOrDefaultAsync(item => item.FeatureId == featureId && item.Version == version, cancellationToken);

    public async Task<IReadOnlyCollection<FeatureVersion>> ListForFeatureAsync(Guid featureId, CancellationToken cancellationToken) =>
        await _context.FeatureVersions
            .Include(version => version.Dependencies)
            .Where(version => version.FeatureId == featureId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<FeatureVersion>> ListBySigningKeyAsync(
        string signingKeyId,
        CancellationToken cancellationToken) =>
        await _context.FeatureVersions
            .Where(version => version.SigningKeyId == signingKeyId)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The whole catalogue in two queries, shaped for the resolver.
    ///
    /// Manifests are parsed here rather than in the resolver so that a version
    /// whose stored manifest is somehow unreadable is simply not a candidate,
    /// instead of throwing in the middle of resolving somebody's install. A
    /// version that cannot be parsed cannot be installed either, so dropping it
    /// is the honest outcome — and it will show up as "no matching version"
    /// rather than a 500.
    /// </summary>
    public async Task<IReadOnlyCollection<RegistryFeature>> GetRegistrySnapshotAsync(CancellationToken cancellationToken)
    {
        var features = await _context.Features
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var versions = await _context.FeatureVersions
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var byFeature = versions.ToLookup(version => version.FeatureId);
        var snapshot = new List<RegistryFeature>(features.Count);

        foreach (var feature in features)
        {
            var parsed = new List<RegistryVersion>();

            foreach (var version in byFeature[feature.Id])
            {
                if (!SemanticVersion.TryParse(version.Version, out var semantic) ||
                    !FeatureManifest.TryParse(version.ManifestJson, out var manifest, out _))
                {
                    continue;
                }

                parsed.Add(new RegistryVersion(version.Id, semantic, manifest, version.IsInstallable));
            }

            snapshot.Add(new RegistryFeature(
                feature.Id,
                feature.Slug,
                feature.Name,
                feature.Status,
                feature.RequiresDedicatedInfrastructure,
                parsed));
        }

        return snapshot;
    }

    public async Task AddAsync(FeatureVersion version, CancellationToken cancellationToken) =>
        await _context.FeatureVersions.AddAsync(version, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class FeatureInstallationRepository : IFeatureInstallationRepository
{
    private readonly ControlPlaneDbContext _context;

    public FeatureInstallationRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<FeatureInstallation?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.FeatureInstallations.FirstOrDefaultAsync(installation => installation.Id == id, cancellationToken);

    public Task<FeatureInstallation?> FindAsync(Guid storeId, Guid featureId, CancellationToken cancellationToken) =>
        _context.FeatureInstallations.FirstOrDefaultAsync(
            installation => installation.StoreId == storeId && installation.FeatureId == featureId,
            cancellationToken);

    public async Task<IReadOnlyCollection<FeatureInstallation>> ListForStoreAsync(Guid storeId, CancellationToken cancellationToken) =>
        await _context.FeatureInstallations
            .Where(installation => installation.StoreId == storeId)
            .OrderBy(installation => installation.FeatureSlug)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<FeatureInstallation>> ListForCustomerFeatureAsync(
        Guid customerId,
        Guid featureId,
        CancellationToken cancellationToken) =>
        await _context.FeatureInstallations
            .Where(installation => installation.CustomerId == customerId && installation.FeatureId == featureId)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyCollection<FeatureInstallation> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        InstallationState? state,
        CancellationToken cancellationToken)
    {
        var query = _context.FeatureInstallations.AsQueryable();

        if (storeId is { } store)
        {
            query = query.Where(installation => installation.StoreId == store);
        }

        if (customerId is { } customer)
        {
            query = query.Where(installation => installation.CustomerId == customer);
        }

        if (state is { } wanted)
        {
            query = query.Where(installation => installation.State == wanted);
        }

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderBy(installation => installation.FeatureSlug)
            .ThenBy(installation => installation.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyCollection<FeatureInstallation>> ListPurgeableAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken) =>
        await _context.FeatureInstallations
            .Where(installation =>
                installation.DataRetainedUntil != null &&
                installation.DataRetainedUntil <= asOf &&
                installation.State == InstallationState.NotInstalled)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(FeatureInstallation installation, CancellationToken cancellationToken) =>
        await _context.FeatureInstallations.AddAsync(installation, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class FeatureInstallationJobRepository : IFeatureInstallationJobRepository
{
    private readonly ControlPlaneDbContext _context;

    public FeatureInstallationJobRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<FeatureInstallationJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.FeatureInstallationJobs
            .Include(job => job.Steps)
            .FirstOrDefaultAsync(job => job.Id == id, cancellationToken);

    public Task<FeatureInstallationJob?> FindByIdempotencyKeyAsync(
        Guid storeId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        _context.FeatureInstallationJobs
            .Include(job => job.Steps)
            .FirstOrDefaultAsync(
                job => job.StoreId == storeId && job.IdempotencyKey == idempotencyKey,
                cancellationToken);

    /// <summary>
    /// The next job for a store, oldest first, and only when nothing is already
    /// running.
    ///
    /// Two agents installing into one Django project at once is a corrupted
    /// virtual environment, not twice the throughput, so a store with a running
    /// job is handed nothing at all rather than the next queued one.
    /// </summary>
    public async Task<FeatureInstallationJob?> FindNextForStoreAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var running = await _context.FeatureInstallationJobs
            .AnyAsync(job => job.StoreId == storeId && job.State == JobState.Running, cancellationToken);

        if (running)
        {
            return null;
        }

        return await _context.FeatureInstallationJobs
            .Include(job => job.Steps)
            .Where(job => job.StoreId == storeId && job.State == JobState.Queued)
            // Ordered by id as well as time, and the tiebreaker is what matters:
            // a plan's jobs are all queued in the same transaction and share a
            // timestamp, so ordering by time alone leaves the database free to
            // hand back a dependent feature before the one it depends on. Job
            // ids are UUIDv7, generated in the plan's topological order, so they
            // carry that order into the queue.
            .OrderBy(job => job.QueuedAt)
            .ThenBy(job => job.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<bool> HasUnfinishedJobAsync(Guid storeId, CancellationToken cancellationToken) =>
        _context.FeatureInstallationJobs.AnyAsync(
            job => job.StoreId == storeId && (job.State == JobState.Queued || job.State == JobState.Running),
            cancellationToken);

    public async Task<(IReadOnlyCollection<FeatureInstallationJob> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        Guid? storeId,
        Guid? customerId,
        JobState? state,
        CancellationToken cancellationToken)
    {
        var query = _context.FeatureInstallationJobs.Include(job => job.Steps).AsQueryable();

        if (storeId is { } store)
        {
            query = query.Where(job => job.StoreId == store);
        }

        if (customerId is { } customer)
        {
            query = query.Where(job => job.CustomerId == customer);
        }

        if (state is { } wanted)
        {
            query = query.Where(job => job.State == wanted);
        }

        var total = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(job => job.QueuedAt)
            .ThenBy(job => job.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyCollection<FeatureInstallationJob>> ListExpiredClaimsAsync(
        DateTimeOffset asOf,
        CancellationToken cancellationToken) =>
        await _context.FeatureInstallationJobs
            .Include(job => job.Steps)
            .Where(job => job.State == JobState.Running && job.ClaimExpiresAt != null && job.ClaimExpiresAt <= asOf)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(FeatureInstallationJob job, CancellationToken cancellationToken) =>
        await _context.FeatureInstallationJobs.AddAsync(job, cancellationToken);

    public void RegisterNewStep(JobStepResult step) =>
        _context.Entry(step).State = EntityState.Added;

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

internal sealed class FeatureConfigurationRepository : IFeatureConfigurationRepository
{
    private readonly ControlPlaneDbContext _context;

    public FeatureConfigurationRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<FeatureDelivery.Domain.FeatureConfiguration?> FindAsync(
        Guid storeId,
        Guid featureId,
        CancellationToken cancellationToken) =>
        _context.FeatureConfigurations.FirstOrDefaultAsync(
            configuration => configuration.StoreId == storeId && configuration.FeatureId == featureId,
            cancellationToken);

    public async Task<IReadOnlyCollection<FeatureDelivery.Domain.FeatureConfiguration>> ListForStoreAsync(
        Guid storeId,
        CancellationToken cancellationToken) =>
        await _context.FeatureConfigurations
            .Where(configuration => configuration.StoreId == storeId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(
        FeatureDelivery.Domain.FeatureConfiguration configuration,
        CancellationToken cancellationToken) =>
        await _context.FeatureConfigurations.AddAsync(configuration, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// Base store images. Platform-wide like the rest of the registry: every store
/// is built from the same published set, whoever owns it.
/// </summary>
internal sealed class StoreImageRepository : IStoreImageRepository
{
    private readonly ControlPlaneDbContext _context;

    public StoreImageRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<StoreImage?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.StoreImages.FirstOrDefaultAsync(image => image.Id == id, cancellationToken);

    public Task<StoreImage?> FindByVersionAsync(string version, CancellationToken cancellationToken) =>
        _context.StoreImages.FirstOrDefaultAsync(image => image.Version == version, cancellationToken);

    public async Task<IReadOnlyCollection<StoreImage>> ListAsync(CancellationToken cancellationToken) =>
        await _context.StoreImages
            .AsNoTracking()
            .OrderByDescending(image => image.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(StoreImage image, CancellationToken cancellationToken) =>
        await _context.StoreImages.AddAsync(image, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// Persistence for staged rollouts.
///
/// Waves and their targets are auto-included by the mapping, because every
/// operation on a rollout is a decision about which stores go next — a rollout
/// loaded without them cannot answer a single useful question.
/// </summary>
internal sealed class FeatureRolloutRepository : IFeatureRolloutRepository
{
    private static readonly RolloutState[] LiveStates =
    [
        RolloutState.Planned,
        RolloutState.InProgress,
        RolloutState.Halted,
    ];

    private readonly ControlPlaneDbContext _context;

    public FeatureRolloutRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<FeatureRollout?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.FeatureRollouts.FirstOrDefaultAsync(rollout => rollout.Id == id, cancellationToken);

    public Task<FeatureRollout?> FindActiveForFeatureAsync(Guid featureId, CancellationToken cancellationToken) =>
        _context.FeatureRollouts
            .FirstOrDefaultAsync(
                rollout => rollout.FeatureId == featureId && LiveStates.Contains(rollout.State),
                cancellationToken);

    public async Task<FeatureRollout?> FindByJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        // Two steps rather than a join through the collection: the target names
        // the wave, the wave names the rollout, and loading the rollout by id
        // brings its waves and targets with it.
        var rolloutId = await _context.RolloutTargets
            .AsNoTracking()
            .Where(target => target.JobId == jobId)
            .Join(
                _context.RolloutWaves.AsNoTracking(),
                target => target.WaveId,
                wave => wave.Id,
                (_, wave) => wave.RolloutId)
            .FirstOrDefaultAsync(cancellationToken);

        return rolloutId == Guid.Empty
            ? null
            : await GetByIdAsync(rolloutId, cancellationToken);
    }

    public async Task<IReadOnlyCollection<FeatureRollout>> ListAdvanceableAsync(CancellationToken cancellationToken) =>
        await _context.FeatureRollouts
            .Where(rollout => rollout.State == RolloutState.InProgress)
            .OrderBy(rollout => rollout.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyCollection<FeatureRollout> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        RolloutState? state,
        CancellationToken cancellationToken)
    {
        var query = _context.FeatureRollouts.AsQueryable();

        if (state is { } wanted)
        {
            query = query.Where(rollout => rollout.State == wanted);
        }

        // Ordered before Skip/Take. A page taken from an unordered query is not a
        // page, it is a sample.
        var ordered = query.OrderByDescending(rollout => rollout.CreatedAt).ThenBy(rollout => rollout.Id);

        var total = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task AddAsync(FeatureRollout rollout, CancellationToken cancellationToken) =>
        await _context.FeatureRollouts.AddAsync(rollout, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}
