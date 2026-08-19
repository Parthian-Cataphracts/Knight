using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace FeatureRegistry.Domain;

/// <summary>
/// One immutable, deployable release of a Feature (docs/feature-delivery.md §3).
///
/// The immutability is the whole point, and it is enforced here rather than by
/// convention. A store that installed <c>1.4.0</c> yesterday and re-runs its
/// installer today must receive byte-identical code; if a published version
/// could be edited, the digest recorded in a store's local registry would stop
/// meaning anything and "which code is actually running" would become
/// unanswerable. So a published version has no setters at all: fixing a bad
/// release means publishing a new one and yanking the old.
///
/// Yanking is not deletion. A yanked version stays readable — stores that
/// already have it installed need to be able to look up what they are running,
/// and the incident that caused the yank needs the record — it simply stops
/// being a candidate for any new installation.
/// </summary>
public sealed class FeatureVersion : AuditableEntity
{
    /// <summary>Length of a lowercase hex sha-256, which is the only digest algorithm accepted.</summary>
    public const int DigestLength = 64;

    public Guid FeatureId { get; private set; }

    /// <summary>The version as written, kept as text so the stored spelling is the published one.</summary>
    public string Version { get; private set; }

    /// <summary>
    /// Where the artifact lives in the package store: the object key of the
    /// signed wheel. KNIGHT is its own index (ADR 0022), so this is a key rather
    /// than a URL — the URL is minted per fetch, short-lived, and never stored.
    /// </summary>
    public string PackageReference { get; private set; }

    /// <summary>Lowercase hex sha-256 of the artifact bytes.</summary>
    public string ArtifactDigest { get; private set; }

    /// <summary>Size in bytes, so a store can refuse a fetch it has no room for during preflight.</summary>
    public long ArtifactSizeBytes { get; private set; }

    /// <summary>Base64 detached Ed25519 signature over the digest.</summary>
    public string Signature { get; private set; }

    /// <summary>
    /// Which key signed it. Recorded per version so that a compromised key can be
    /// revoked and everything it ever signed yanked in one query, which is the
    /// containment property the custody decision was made for (risks.md R21).
    /// </summary>
    public string SigningKeyId { get; private set; }

    /// <summary>The manifest exactly as published, stored as a document.</summary>
    public string ManifestJson { get; private set; }

    public FeatureVersionStatus Status { get; private set; }

    public string? ReleaseNotes { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public DateTimeOffset? YankedAt { get; private set; }

    public Guid? YankedBy { get; private set; }

    /// <summary>Why it was yanked. Required: a yank with no reason is an unexplained outage for whoever reads it next.</summary>
    public string? YankReason { get; private set; }

    private readonly List<FeatureDependency> _dependencies = [];

    /// <summary>
    /// The feature dependencies declared by this version's manifest, denormalised
    /// out of the document so the resolver can walk the graph in SQL rather than
    /// by parsing every manifest in the registry.
    /// </summary>
    public IReadOnlyCollection<FeatureDependency> Dependencies => _dependencies.AsReadOnly();

    private FeatureVersion()
    {
        Version = string.Empty;
        PackageReference = string.Empty;
        ArtifactDigest = string.Empty;
        Signature = string.Empty;
        SigningKeyId = string.Empty;
        ManifestJson = string.Empty;
    }

    private FeatureVersion(
        Guid id,
        DateTimeOffset createdAt,
        Guid featureId,
        string version,
        string packageReference,
        string artifactDigest,
        long artifactSizeBytes,
        string signature,
        string signingKeyId,
        string manifestJson,
        string? releaseNotes)
        : base(id, createdAt)
    {
        FeatureId = featureId;
        Version = version;
        PackageReference = packageReference;
        ArtifactDigest = artifactDigest;
        ArtifactSizeBytes = artifactSizeBytes;
        Signature = signature;
        SigningKeyId = signingKeyId;
        ManifestJson = manifestJson;
        ReleaseNotes = releaseNotes;
        Status = FeatureVersionStatus.Draft;
    }

    public static FeatureVersion Create(
        Guid id,
        DateTimeOffset createdAt,
        Guid featureId,
        FeatureManifest manifest,
        string manifestJson,
        string packageReference,
        string artifactDigest,
        long artifactSizeBytes,
        string signature,
        string signingKeyId,
        string? releaseNotes)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var candidate = new FeatureVersion(
            id,
            createdAt,
            featureId,
            manifest.Version.ToString(),
            RequireText(packageReference, "package reference", 500),
            RequireDigest(artifactDigest),
            RequirePositiveSize(artifactSizeBytes),
            RequireText(signature, "signature", 1000),
            RequireText(signingKeyId, "signing key id", 100),
            RequireText(manifestJson, "manifest", int.MaxValue),
            string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes.Trim());

        foreach (var dependency in manifest.Dependencies.Features)
        {
            candidate._dependencies.Add(FeatureDependency.Create(
                Guid.CreateVersion7(),
                candidate.Id,
                dependency.Slug,
                dependency.Version.Expression));
        }

        return candidate;
    }

    /// <summary>
    /// Makes the version installable.
    ///
    /// Publishing is the moment the version stops being editable, so everything
    /// that must be true of a deliverable artifact is checked here and not at
    /// install time: an unsigned artifact never becomes publishable later, and a
    /// store discovering the problem mid-install is a store already halfway
    /// through a migration.
    /// </summary>
    public void Publish(Guid publishedBy, DateTimeOffset now)
    {
        if (Status is not FeatureVersionStatus.Draft)
        {
            throw DomainException.Conflict($"A version in status '{Status}' cannot be published.");
        }

        if (string.IsNullOrWhiteSpace(Signature) || string.IsNullOrWhiteSpace(SigningKeyId))
        {
            throw DomainException.Conflict("An unsigned artifact cannot be published.");
        }

        Status = FeatureVersionStatus.Published;
        PublishedAt = now;
        PublishedBy = publishedBy;
        MarkUpdated(now);
    }

    /// <summary>
    /// Withdraws the version from any future installation. Existing
    /// installations are deliberately untouched: pulling working code out from
    /// under a running store is a worse outage than the one being fixed, so a
    /// yank stops new installs and raises the alert that makes the upgrade
    /// somebody's job.
    /// </summary>
    public void Yank(Guid yankedBy, string reason, DateTimeOffset now)
    {
        if (Status is not FeatureVersionStatus.Published)
        {
            throw DomainException.Conflict($"A version in status '{Status}' cannot be yanked.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw DomainException.Validation("A yank must say why.");
        }

        Status = FeatureVersionStatus.Yanked;
        YankedAt = now;
        YankedBy = yankedBy;
        YankReason = reason.Trim();
        MarkUpdated(now);
    }

    /// <summary>Release notes are the one field a published version still allows: they document, they do not deliver.</summary>
    public void UpdateReleaseNotes(string? releaseNotes, DateTimeOffset now)
    {
        ReleaseNotes = string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes.Trim();
        MarkUpdated(now);
    }

    /// <summary>True when a new installation may resolve to this version.</summary>
    public bool IsInstallable => Status is FeatureVersionStatus.Published;

    public SemanticVersion SemanticVersion => SemanticVersion.Parse(Version);

    private static string RequireDigest(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            throw DomainException.Validation("An artifact digest is required.");
        }

        var normalized = digest.Trim().ToLowerInvariant();

        // The digest is what the store verifies the downloaded bytes against, so
        // a malformed one is not a cosmetic problem: it is a verification step
        // that would either always fail or, worse, be skipped.
        if (normalized.Length != DigestLength)
        {
            throw DomainException.Validation("An artifact digest must be a hex-encoded sha-256.");
        }

        foreach (var character in normalized)
        {
            if (!char.IsAsciiDigit(character) && character is < 'a' or > 'f')
            {
                throw DomainException.Validation("An artifact digest must be a hex-encoded sha-256.");
            }
        }

        return normalized;
    }

    private static long RequirePositiveSize(long size) =>
        size > 0 ? size : throw DomainException.Validation("An artifact must have a non-zero size.");

    private static string RequireText(string value, string what, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw DomainException.Validation($"A {what} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw DomainException.Validation($"The {what} is too long.");
        }

        return trimmed;
    }
}

public enum FeatureVersionStatus
{
    Draft = 0,
    Published = 1,
    Yanked = 2,
}
