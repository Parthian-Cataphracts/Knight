using System.Text.Json;
using FeatureDelivery.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Answers the four delivery questions the observability rules ask.
///
/// Every one of them is a comparison between records owned by different modules
/// — what a customer is entitled to versus what is installed, what KNIGHT
/// intended versus what the store reports on disk, when a job was claimed versus
/// now. None of those belongs to a single module, which is why they are read
/// here, in the one place that can see the whole schema, rather than by giving
/// one module a reference to another (docs/README.md, rule 3).
///
/// These run in platform scope on a timer, so they deliberately see every
/// customer. The endpoints that expose the resulting alerts do not.
/// </summary>
internal sealed class DeliveryHealthReader : IDeliveryHealthReader
{
    private readonly ControlPlaneDbContext _context;
    private readonly ILogger<DeliveryHealthReader> _logger;

    public DeliveryHealthReader(ControlPlaneDbContext context, ILogger<DeliveryHealthReader> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Entitlements with no serving installation on a store that should have one.
    ///
    /// The grace cutoff is what keeps this from alerting on the system working
    /// normally: installation is asynchronous, so an entitlement granted a minute
    /// ago being uninstalled is the expected state, not a fault. Only one that has
    /// stayed that way past the grace period means something is actually wrong.
    /// </summary>
    public async Task<IReadOnlyCollection<DeliveryDiscrepancy>> ListEntitledNotInstalledAsync(
        DateTimeOffset graceCutoff,
        CancellationToken cancellationToken)
    {
        // Live entitlements: granted, not revoked, not expired, and old enough
        // that delivery has had a fair chance to act on them.
        var entitlements = await _context.FeatureEntitlements
            .AsNoTracking()
            .Where(entitlement => entitlement.RevokedAt == null &&
                                  entitlement.GrantedAt <= graceCutoff &&
                                  (entitlement.ExpiresAt == null || entitlement.ExpiresAt > graceCutoff))
            .Select(entitlement => new { entitlement.CustomerId, entitlement.FeatureId })
            .ToArrayAsync(cancellationToken);

        if (entitlements.Length == 0)
        {
            return [];
        }

        var customerIds = entitlements.Select(entitlement => entitlement.CustomerId).Distinct().ToArray();

        // Only stores that are actually meant to be running something. An
        // archived store has no installation gap, and a suspended one is not
        // supposed to be serving — alerting on either would be reporting the
        // system doing what it was told.
        var stores = await _context.Stores
            .AsNoTracking()
            .Where(store => customerIds.Contains(store.CustomerId) &&
                            store.Status == Stores.Domain.StoreStatus.Active)
            .Select(store => new { store.Id, store.CustomerId, store.Name })
            .ToArrayAsync(cancellationToken);

        if (stores.Length == 0)
        {
            return [];
        }

        var storeIds = stores.Select(store => store.Id).ToArray();

        var installations = await _context.FeatureInstallations
            .AsNoTracking()
            .Where(installation => storeIds.Contains(installation.StoreId))
            .Select(installation => new
            {
                installation.StoreId,
                installation.FeatureId,
                installation.State,
                installation.FeatureSlug,
            })
            .ToArrayAsync(cancellationToken);

        var slugs = await FeatureSlugsAsync(
            entitlements.Select(entitlement => entitlement.FeatureId).Distinct().ToArray(),
            cancellationToken);

        var byStoreAndFeature = installations.ToDictionary(
            installation => (installation.StoreId, installation.FeatureId));

        var discrepancies = new List<DeliveryDiscrepancy>();

        foreach (var store in stores)
        {
            foreach (var entitlement in entitlements.Where(entitlement => entitlement.CustomerId == store.CustomerId))
            {
                var found = byStoreAndFeature.TryGetValue((store.Id, entitlement.FeatureId), out var installation);

                // Installed and Disabled both count as delivered: a disabled
                // feature is code that is present and deliberately switched off,
                // which is a different situation entirely from code that is not
                // there (adr/0016).
                if (found && installation!.State is not
                        (InstallationState.NotInstalled or InstallationState.Failed))
                {
                    continue;
                }

                var slug = slugs.GetValueOrDefault(entitlement.FeatureId, entitlement.FeatureId.ToString());

                discrepancies.Add(new DeliveryDiscrepancy(
                    // The subject is the store and feature pair, expressed as a
                    // stable id, so that re-detecting the same gap next pass
                    // deduplicates onto one alert rather than raising another.
                    SubjectFor(store.Id, entitlement.FeatureId),
                    store.Id,
                    store.CustomerId,
                    store.Name,
                    slug,
                    found
                        ? $"The installation is in state {installation!.State}."
                        : "No installation record exists at all."));
            }
        }

        return discrepancies;
    }

    /// <summary>
    /// Installations whose store reports something other than what KNIGHT
    /// installed.
    ///
    /// The store's own health check is the source of truth for what is on disk;
    /// the installation record is what KNIGHT intended. When they disagree, the
    /// control plane's picture of that store is wrong, and every later decision
    /// it makes about that store is built on the wrong picture.
    /// </summary>
    public async Task<IReadOnlyCollection<DeliveryDiscrepancy>> ListDriftedAsync(CancellationToken cancellationToken)
    {
        var installed = await _context.FeatureInstallations
            .AsNoTracking()
            .Where(installation => installation.State == InstallationState.Installed &&
                                   installation.InstalledVersion != null)
            .Select(installation => new
            {
                installation.Id,
                installation.StoreId,
                installation.CustomerId,
                installation.FeatureSlug,
                installation.InstalledVersion,
            })
            .ToArrayAsync(cancellationToken);

        if (installed.Length == 0)
        {
            return [];
        }

        var storeIds = installed.Select(installation => installation.StoreId).Distinct().ToArray();

        // The latest health check per store. Grouped in the database rather than
        // loaded and grouped here: the health-check table is append-only and
        // large, and this runs every minute.
        var latest = await _context.StoreHealthChecks
            .AsNoTracking()
            .Where(check => storeIds.Contains(check.StoreId) && check.ReportedFeatures != null)
            .GroupBy(check => check.StoreId)
            .Select(group => group
                .OrderByDescending(check => check.CheckedAt)
                .Select(check => new { check.StoreId, check.ReportedFeatures })
                .First())
            .ToArrayAsync(cancellationToken);

        var names = await StoreNamesAsync(storeIds, cancellationToken);

        var reported = latest.ToDictionary(
            check => check.StoreId,
            check => ParseReportedFeatures(check.StoreId, check.ReportedFeatures));

        var discrepancies = new List<DeliveryDiscrepancy>();

        foreach (var installation in installed)
        {
            if (!reported.TryGetValue(installation.StoreId, out var onDisk))
            {
                // The store has never reported its feature set. That is a gap in
                // observation, not evidence of drift, and alerting on it would
                // fire for every store the moment health reporting broke.
                continue;
            }

            if (!onDisk.TryGetValue(installation.FeatureSlug, out var version))
            {
                discrepancies.Add(new DeliveryDiscrepancy(
                    installation.Id,
                    installation.StoreId,
                    installation.CustomerId,
                    names.GetValueOrDefault(installation.StoreId, installation.StoreId.ToString()),
                    installation.FeatureSlug,
                    $"as absent, but KNIGHT installed {installation.InstalledVersion}."));

                continue;
            }

            if (!string.Equals(version, installation.InstalledVersion, StringComparison.OrdinalIgnoreCase))
            {
                discrepancies.Add(new DeliveryDiscrepancy(
                    installation.Id,
                    installation.StoreId,
                    installation.CustomerId,
                    names.GetValueOrDefault(installation.StoreId, installation.StoreId.ToString()),
                    installation.FeatureSlug,
                    $"at {version}, but KNIGHT installed {installation.InstalledVersion}."));
            }
        }

        return discrepancies;
    }

    public async Task<IReadOnlyCollection<DeliveryDiscrepancy>> ListStuckJobsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var jobs = await _context.FeatureInstallationJobs
            .AsNoTracking()
            .Where(job => job.State == JobState.Running &&
                          job.ClaimedAt != null &&
                          job.ClaimedAt < cutoff)
            .Select(job => new
            {
                job.Id,
                job.StoreId,
                job.CustomerId,
                job.FeatureSlug,
                job.Type,
                job.ClaimedAt,
            })
            .ToArrayAsync(cancellationToken);

        return await DescribeAsync(
            jobs.Select(job => (
                job.Id,
                job.StoreId,
                job.CustomerId,
                Slug: $"{job.Type}",
                Detail: $"It was claimed at {job.ClaimedAt:u} and covers {job.FeatureSlug}.")),
            cancellationToken);
    }

    public async Task<IReadOnlyCollection<DeliveryDiscrepancy>> ListFailedJobsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var jobs = await _context.FeatureInstallationJobs
            .AsNoTracking()
            .Where(job => job.State == JobState.Failed && job.CompletedAt != null && job.CompletedAt >= since)
            .Select(job => new
            {
                job.Id,
                job.StoreId,
                job.CustomerId,
                job.FeatureSlug,
                job.FailureCode,
                job.FailureMessage,
            })
            .ToArrayAsync(cancellationToken);

        return await DescribeAsync(
            jobs.Select(job => (
                job.Id,
                job.StoreId,
                job.CustomerId,
                Slug: job.FeatureSlug,
                Detail: job.FailureMessage ?? job.FailureCode ?? "No reason was recorded.")),
            cancellationToken);
    }

    /// <summary>
    /// What stores have reported about delivering to a Feature's service.
    ///
    /// Read out of the error events stores already send, because that is the
    /// channel they already have and a second one would be a second thing to be
    /// down. The kinds are a closed list the two ends agree on by name
    /// (<see cref="StoreFailureKinds"/>), never a prefix match — a store's own
    /// exception whose type happened to start with `knight.` must not become a
    /// platform alert.
    ///
    /// Grouped here rather than in the rule: a service that has been down for an
    /// hour produced hundreds of rows, the fact is one fact, and carrying the
    /// hundreds into memory to count them there would be the same query done
    /// worse.
    /// </summary>
    public async Task<IReadOnlyCollection<StoreReportedFailure>> ListStoreReportedFailuresAsync(
        DateTimeOffset since,
        IReadOnlyCollection<string> kinds,
        CancellationToken cancellationToken)
    {
        if (kinds.Count == 0)
        {
            return [];
        }

        var wanted = kinds.ToArray();

        var rows = await _context.StoreErrorEvents
            .AsNoTracking()
            .Where(row => row.OccurredAt >= since && wanted.Contains(row.ExceptionType))
            .Select(row => new
            {
                row.ExceptionType,
                row.StoreId,
                row.CustomerId,
                row.OccurredAt,
                row.Message,
                row.Context,
            })
            .ToArrayAsync(cancellationToken);

        if (rows.Length == 0)
        {
            return [];
        }

        var names = await StoreNamesAsync(
            rows.Select(row => row.StoreId).Distinct().ToArray(),
            cancellationToken);

        return rows
            .GroupBy(row => new { row.ExceptionType, row.StoreId, row.CustomerId, Feature = FeatureOf(row.Context) })
            .Select(group =>
            {
                var newest = group.OrderByDescending(row => row.OccurredAt).First();

                return new StoreReportedFailure(
                    group.Key.ExceptionType,
                    group.Key.StoreId,
                    group.Key.CustomerId,
                    names.GetValueOrDefault(group.Key.StoreId, group.Key.StoreId.ToString()),
                    group.Key.Feature,
                    group.Count(),
                    newest.OccurredAt,
                    newest.Message);
            })
            .ToArray();
    }

    /// <summary>
    /// Which Feature a report was about, out of the context the store sent.
    ///
    /// Unknown rather than a guess when it is absent or unreadable. A report
    /// attributed to the wrong Feature is worse than one attributed to none:
    /// somebody would go and look at it.
    /// </summary>
    private static string FeatureOf(string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return "unknown";
        }

        try
        {
            using var document = JsonDocument.Parse(context);

            return document.RootElement.TryGetProperty("feature", out var feature)
                && feature.ValueKind is JsonValueKind.String
                && feature.GetString() is { Length: > 0 } slug
                    ? slug
                    : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    /// <summary>Attaches store names, which every message needs and no query above has.</summary>
    private async Task<IReadOnlyCollection<DeliveryDiscrepancy>> DescribeAsync(
        IEnumerable<(Guid Id, Guid StoreId, Guid CustomerId, string Slug, string Detail)> rows,
        CancellationToken cancellationToken)
    {
        var materialised = rows.ToArray();

        if (materialised.Length == 0)
        {
            return [];
        }

        var names = await StoreNamesAsync(
            materialised.Select(row => row.StoreId).Distinct().ToArray(),
            cancellationToken);

        return materialised
            .Select(row => new DeliveryDiscrepancy(
                row.Id,
                row.StoreId,
                row.CustomerId,
                names.GetValueOrDefault(row.StoreId, row.StoreId.ToString()),
                row.Slug,
                row.Detail))
            .ToArray();
    }

    private async Task<Dictionary<Guid, string>> StoreNamesAsync(
        IReadOnlyCollection<Guid> storeIds,
        CancellationToken cancellationToken) =>
        await _context.Stores
            .AsNoTracking()
            .Where(store => storeIds.Contains(store.Id))
            .ToDictionaryAsync(store => store.Id, store => store.Name, cancellationToken);

    private async Task<Dictionary<Guid, string>> FeatureSlugsAsync(
        IReadOnlyCollection<Guid> featureIds,
        CancellationToken cancellationToken) =>
        await _context.Features
            .AsNoTracking()
            .Where(feature => featureIds.Contains(feature.Id))
            .ToDictionaryAsync(feature => feature.Id, feature => feature.Slug, cancellationToken);

    /// <summary>
    /// Reads the store's reported feature set.
    ///
    /// Both shapes stores actually send are accepted: an object of slug to
    /// version, and a bare array of slugs from a store too old to report
    /// versions. Malformed JSON yields an empty set and a log line rather than an
    /// exception — this comes from outside KNIGHT, and a store shipping bad JSON
    /// must not stop the drift sweep running for everybody else.
    /// </summary>
    private Dictionary<string, string> ParseReportedFeatures(Guid storeId, string? json)
    {
        var features = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(json))
        {
            return features;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        features[property.Name] = property.Value.ValueKind is JsonValueKind.String
                            ? property.Value.GetString() ?? string.Empty
                            : property.Value.ToString();
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind is JsonValueKind.String && element.GetString() is { } slug)
                        {
                            // No version reported. Recorded as present with an
                            // unknown version rather than skipped, so the feature
                            // does not look absent and raise a false drift alert.
                            features[slug] = string.Empty;
                        }
                        else if (element.ValueKind is JsonValueKind.Object &&
                                 element.TryGetProperty("slug", out var slugProperty) &&
                                 slugProperty.GetString() is { } objectSlug)
                        {
                            features[objectSlug] = element.TryGetProperty("version", out var versionProperty)
                                ? versionProperty.GetString() ?? string.Empty
                                : string.Empty;
                        }
                    }

                    break;
            }
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Store {StoreId} reported a feature set that is not valid JSON; drift cannot be evaluated for it.",
                storeId);
        }

        return features;
    }

    /// <summary>
    /// A stable id for a store-and-feature pair, so the same gap detected on two
    /// consecutive passes deduplicates onto one alert.
    ///
    /// Derived rather than stored: there is no row for "an entitlement that was
    /// never installed" — the absence is the whole point — so the identity has to
    /// come from the two things that are present.
    /// </summary>
    private static Guid SubjectFor(Guid storeId, Guid featureId)
    {
        Span<byte> material = stackalloc byte[32];

        storeId.TryWriteBytes(material[..16]);
        featureId.TryWriteBytes(material[16..]);

        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(material, hash);

        return new Guid(hash[..16]);
    }
}
