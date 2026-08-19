using FeatureDelivery;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Stores.Domain;

namespace Knight.Infrastructure.ControlPlane.Adapters;

/// <summary>
/// Reads the facts about a store that delivery needs, without the delivery
/// engine having to know the Stores module exists.
/// </summary>
internal sealed class StoreDeliveryReader : IStoreDeliveryReader
{
    private readonly ControlPlaneDbContext _context;

    public StoreDeliveryReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<FeaturePlanContext?> GetPlanContextAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var store = await _context.Stores
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == storeId, cancellationToken);

        if (store is null)
        {
            return null;
        }

        // The installed set comes from KNIGHT's own installation records rather
        // than from what the store last reported. The two can disagree — that
        // disagreement is drift, and phase 5 alerts on it — but a plan must be
        // built from the record the control plane is willing to be held to.
        var installed = await _context.FeatureInstallations
            .AsNoTracking()
            .Where(installation =>
                installation.StoreId == storeId &&
                installation.InstalledVersion != null)
            .Select(installation => new { installation.FeatureSlug, installation.InstalledVersion })
            .ToListAsync(cancellationToken);

        var runtime = await ReadRuntimeAsync(storeId, cancellationToken);

        return new FeaturePlanContext(
            storeId,
            store.ApplicationVersion,
            runtime.Python,
            runtime.Django,
            store.HostingModel is not HostingModel.SharedManaged,
            installed.ToDictionary(
                entry => entry.FeatureSlug,
                entry => entry.InstalledVersion!,
                StringComparer.Ordinal));
    }

    public async Task<Guid?> GetOwningCustomerAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var owner = await _context.Stores
            .AsNoTracking()
            .Where(store => store.Id == storeId)
            .Select(store => (Guid?)store.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);

        return owner;
    }

    /// <summary>
    /// Digs the Python and Django versions out of the most recent health check.
    ///
    /// A store reports its runtime as part of its health payload, so the newest
    /// check is the freshest answer available. When it has never reported one, the
    /// answer is null — and the resolver treats null as "cannot certify" rather
    /// than "no objection", which is the whole reason this returns null instead of
    /// a guess.
    /// </summary>
    private async Task<(string? Python, string? Django)> ReadRuntimeAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var latest = await _context.StoreHealthChecks
            .AsNoTracking()
            .Where(check => check.StoreId == storeId && check.Dependencies != null)
            .OrderByDescending(check => check.CheckedAt)
            .Select(check => check.Dependencies)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(latest))
        {
            return (null, null);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(latest);
            var root = document.RootElement;

            return (
                root.TryGetProperty("python", out var python) ? python.GetString() : null,
                root.TryGetProperty("django", out var django) ? django.GetString() : null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }
    }
}

/// <summary>
/// Hands delivery the few facts about a published version it needs to send an
/// agent to fetch and verify one.
///
/// It lives in the infrastructure layer rather than in either module because it
/// is exactly a join between them, and the two modules are not allowed to know
/// about each other.
/// </summary>
internal sealed class FeatureVersionReader : IFeatureVersionReader
{
    private readonly ControlPlaneDbContext _context;

    public FeatureVersionReader(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<DeliverableVersion?> GetForDeliveryAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var version = await _context.FeatureVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == versionId, cancellationToken);

        if (version is null)
        {
            return null;
        }

        var slug = await _context.Features
            .AsNoTracking()
            .Where(feature => feature.Id == version.FeatureId)
            .Select(feature => feature.Slug)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        // The migration policy is read out of the stored manifest rather than
        // duplicated into a column: the manifest is what the author signed, and a
        // column could drift from it.
        var migrations = MigrationPolicy.None;
        var retentionDays = 30;

        if (FeatureManifest.TryParse(version.ManifestJson, out var manifest, out _))
        {
            migrations = manifest.Migrations;
            retentionDays = manifest.Uninstall.DataRetentionDays;
        }

        return new DeliverableVersion(
            version.Id,
            slug,
            version.Version,
            version.PackageReference,
            version.ArtifactDigest,
            version.ArtifactSizeBytes,
            version.Signature,
            version.SigningKeyId,
            migrations.Required,
            migrations.Reversible,
            migrations.RequiresMaintenanceWindow,
            retentionDays);
    }
}
