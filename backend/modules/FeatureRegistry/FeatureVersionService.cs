using System.Text.Json;
using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace FeatureRegistry;

/// <summary>
/// Publishing and yanking versions.
///
/// The order of the checks in <see cref="CreateAsync"/> is the point of this
/// class. A manifest is parsed before anything else because every later check
/// needs to know what it claims to be; the artifact is confirmed to exist and to
/// hash to the declared digest before the signature is checked, because a
/// signature over a digest nobody verified proves nothing about the bytes a store
/// will receive; and dependencies are resolved last, because a version whose
/// dependencies are missing is a legitimate thing to refuse but a pointless thing
/// to compute for an artifact that was never going to be accepted.
/// </summary>
internal sealed class FeatureVersionService : IFeatureVersionService
{
    private const int MaxPageSize = 100;

    private readonly IFeatureVersionRepository _versions;
    private readonly IFeatureRepository _features;
    private readonly IFeatureArtifactSigner _signer;
    private readonly IFeatureArtifactStore _artifacts;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUser _currentUser;

    public FeatureVersionService(
        IFeatureVersionRepository versions,
        IFeatureRepository features,
        IFeatureArtifactSigner signer,
        IFeatureArtifactStore artifacts,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ICurrentUser currentUser)
    {
        _versions = versions;
        _features = features;
        _signer = signer;
        _artifacts = artifacts;
        _audit = audit;
        _clock = clock;
        _currentUser = currentUser;
    }

    public ManifestValidationResult ValidateManifest(string manifestJson)
    {
        if (FeatureManifest.TryParse(manifestJson, out var manifest, out var errors))
        {
            return new ManifestValidationResult(true, manifest.Slug, manifest.Version.ToString(), []);
        }

        return new ManifestValidationResult(false, null, null, errors);
    }

    public async Task<FeatureVersionPage> ListAsync(Guid featureId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize is < 1 or > MaxPageSize ? 25 : pageSize;

        var all = await _versions.ListForFeatureAsync(featureId, cancellationToken);

        // Newest first: the version somebody is looking for is almost always the
        // latest one, and the list is bounded by how often a feature is released.
        var ordered = all
            .OrderByDescending(version => version.SemanticVersion, Comparer<Knight.Domain.Versioning.SemanticVersion>.Default)
            .ToList();

        var items = ordered.Skip((safePage - 1) * safeSize).Take(safeSize).ToList();
        return new FeatureVersionPage(items, safePage, safeSize, ordered.Count);
    }

    public Task<FeatureVersion?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _versions.GetByIdAsync(id, cancellationToken);

    public async Task<FeatureVersion> CreateAsync(PublishVersionInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (!FeatureManifest.TryParse(input.ManifestJson, out var manifest, out var errors))
        {
            // The manifest reader already names the JSON path of every bad
            // field, which is exactly the shape a validation problem wants.
            throw new ValidationException(errors
                .GroupBy(error => error.Path, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).ToArray(),
                    StringComparer.Ordinal));
        }

        var feature = await _features.GetBySlugAsync(manifest.Slug, cancellationToken)
            ?? throw new NotFoundException($"No feature is registered with slug '{manifest.Slug}'.");

        if (feature.Status is FeatureStatus.Withdrawn)
        {
            throw new ConflictException($"'{manifest.Slug}' is withdrawn; no new version can be published for it.");
        }

        var version = manifest.Version.ToString();
        if (await _versions.FindAsync(feature.Id, version, cancellationToken) is not null)
        {
            // A published version is immutable, so republishing is not an update
            // — it is an attempt to change code a store may already be running.
            throw new ConflictException(
                $"Version {version} of '{manifest.Slug}' already exists. A published version is immutable: publish a new version and yank this one.");
        }

        var artifact = await _artifacts.FindAsync(input.PackageReference, cancellationToken)
            ?? throw Invalid(
                "packageReference",
                $"No artifact was found at '{input.PackageReference}'. Upload the package before publishing its version.");

        var declaredDigest = input.ArtifactDigest.Trim().ToLowerInvariant();
        if (!string.Equals(artifact.Digest, declaredDigest, StringComparison.Ordinal))
        {
            // The digest is what a store verifies its download against. If it
            // disagrees with the stored object now, every install of this version
            // would fail verification — or, if the check were skipped, install
            // bytes nobody vouched for.
            throw Invalid(
                "artifactDigest",
                "The declared artifact digest does not match the uploaded package. The artifact may be corrupt or the wrong file.");
        }

        var signingKeyId = string.IsNullOrWhiteSpace(input.SigningKeyId) ? _signer.ActiveKeyId : input.SigningKeyId.Trim();

        if (!_signer.Verify(declaredDigest, input.Signature, signingKeyId))
        {
            throw Invalid(
                "signature",
                $"The signature over the artifact digest is not valid for signing key '{signingKeyId}'.");
        }

        var candidate = FeatureVersion.Create(
            Guid.CreateVersion7(),
            now,
            feature.Id,
            manifest,
            Canonicalise(input.ManifestJson),
            input.PackageReference,
            declaredDigest,
            artifact.SizeBytes,
            input.Signature,
            signingKeyId,
            input.ReleaseNotes);

        await EnsureDependenciesResolveAsync(manifest, cancellationToken);

        await _versions.AddAsync(candidate, cancellationToken);
        await _versions.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.version.created",
            "FeatureVersion",
            candidate.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { manifest.Slug, Version = version, candidate.ArtifactDigest, candidate.SigningKeyId });

        return candidate;
    }

    public async Task<FeatureVersion> PublishAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var version = await RequireAsync(versionId, cancellationToken);

        version.Publish(_currentUser.UserId ?? Guid.Empty, _clock.UtcNow);
        await _versions.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.version.published",
            "FeatureVersion",
            version.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { version.FeatureId, version.Version, version.ArtifactDigest });

        return version;
    }

    public async Task<FeatureVersion> YankAsync(Guid versionId, string reason, CancellationToken cancellationToken)
    {
        var version = await RequireAsync(versionId, cancellationToken);

        version.Yank(_currentUser.UserId ?? Guid.Empty, reason, _clock.UtcNow);
        await _versions.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.version.yanked",
            "FeatureVersion",
            version.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { version.FeatureId, version.Version, Reason = reason });

        return version;
    }

    public async Task<int> YankBySigningKeyAsync(string signingKeyId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(signingKeyId))
        {
            throw Invalid("signingKeyId", "A signing key id is required.");
        }

        var affected = await _versions.ListBySigningKeyAsync(signingKeyId.Trim(), cancellationToken);
        var now = _clock.UtcNow;
        var actor = _currentUser.UserId ?? Guid.Empty;
        var yanked = 0;

        foreach (var version in affected)
        {
            // Drafts and already-yanked versions are skipped rather than treated
            // as failures: the operation has to be safe to re-run during an
            // incident, and a key compromise is exactly when nobody should be
            // debugging a half-finished containment step.
            if (version.Status is not FeatureVersionStatus.Published)
            {
                continue;
            }

            version.Yank(actor, reason, now);
            yanked++;
        }

        await _versions.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "feature.signing_key.revoked",
            "SigningKey",
            signingKeyId,
            null,
            cancellationToken,
            newValue: new { SigningKeyId = signingKeyId, Reason = reason, YankedVersions = yanked });

        return yanked;
    }

    /// <summary>
    /// Refuses a version whose declared dependencies cannot be satisfied by the
    /// registry as it stands.
    ///
    /// Checked at publish rather than at install because a version that can never
    /// be installed anywhere is a broken release, and the person who can fix it is
    /// the one running the publish — not the customer whose install fails next
    /// week (docs/adr/0017-feature-compatibility-and-dependencies.md).
    /// </summary>
    private async Task EnsureDependenciesResolveAsync(FeatureManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.Dependencies.Features.Count == 0)
        {
            return;
        }

        var snapshot = await _versions.GetRegistrySnapshotAsync(cancellationToken);
        var bySlug = snapshot.ToDictionary(feature => feature.Slug, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var dependency in manifest.Dependencies.Features)
        {
            if (dependency.Slug == manifest.Slug)
            {
                problems.Add($"'{manifest.Slug}' declares a dependency on itself.");
                continue;
            }

            if (!bySlug.TryGetValue(dependency.Slug, out var target))
            {
                problems.Add($"'{dependency.Slug}' is not registered.");
                continue;
            }

            var installable = target.Versions
                .Where(candidate => candidate.IsInstallable)
                .Select(candidate => candidate.Version)
                .ToList();

            if (dependency.Version.BestMatch(installable) is null)
            {
                var available = installable.Count == 0
                    ? "none are published"
                    : string.Join(", ", installable.Select(item => item.ToString()).Order(StringComparer.Ordinal));

                problems.Add($"no published version of '{dependency.Slug}' satisfies '{dependency.Version}' ({available}).");
            }
        }

        if (problems.Count > 0)
        {
            throw new ValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["dependencies"] = [.. problems] });
        }
    }

    /// <summary>
    /// Reduces the manifest to its canonical JSON form before storing it, so that
    /// two publishes differing only in whitespace produce the same stored
    /// document.
    /// </summary>
    private static string Canonicalise(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static ValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    private async Task<FeatureVersion> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await _versions.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Feature version '{id}' was not found.");
}
