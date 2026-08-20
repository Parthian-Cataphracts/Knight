using Knight.Domain.Common;
using Knight.Domain.Exceptions;
using Knight.Domain.Versioning;

namespace FeatureRegistry.Domain;

/// <summary>
/// One published version of the base store image
/// (docs/store-provisioning.md §3).
///
/// The image is the store skeleton plus the `knight_integration` layer and
/// nothing else: no business Features, which arrive as ordinary deployable
/// packages afterwards ([`adr/0024`](../../../docs/adr/0024-base-store-versus-optional-feature.md)).
/// It is versioned, signed and digest-verified exactly like a Feature artifact,
/// and for the same reason — it is executable code that will run somebody's
/// shop, and the control plane is the thing that vouches for it.
///
/// It carries the <see cref="StoreVersion"/> the built instance reports, which is
/// what Feature compatibility ranges are checked against. That is why the image
/// belongs in the registry rather than in a wiki page: "which store version does
/// image 2.3.0 produce" is a question the resolver needs answered, not a
/// deployment note.
/// </summary>
public sealed class StoreImage : AuditableEntity
{
    public const int DigestLength = 64;

    /// <summary>The image version, as published.</summary>
    public string Version { get; private set; }

    /// <summary>
    /// The <c>storeVersion</c> an instance built from this image reports. Feature
    /// compatibility ranges are resolved against it, so it is recorded here
    /// rather than discovered from a running store.
    /// </summary>
    public string StoreVersion { get; private set; }

    public string PackageReference { get; private set; }

    /// <summary>Lowercase hex sha-256 of the image artifact.</summary>
    public string ArtifactDigest { get; private set; }

    public long ArtifactSizeBytes { get; private set; }

    /// <summary>Base64 detached signature over the digest, made offline by the packaging tool.</summary>
    public string Signature { get; private set; }

    public string SigningKeyId { get; private set; }

    public StoreImageStatus Status { get; private set; }

    public string? ReleaseNotes { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public DateTimeOffset? YankedAt { get; private set; }

    public string? YankReason { get; private set; }

    private StoreImage()
    {
        Version = string.Empty;
        StoreVersion = string.Empty;
        PackageReference = string.Empty;
        ArtifactDigest = string.Empty;
        Signature = string.Empty;
        SigningKeyId = string.Empty;
    }

    private StoreImage(
        Guid id,
        DateTimeOffset createdAt,
        string version,
        string storeVersion,
        string packageReference,
        string artifactDigest,
        long artifactSizeBytes,
        string signature,
        string signingKeyId,
        string? releaseNotes)
        : base(id, createdAt)
    {
        Version = version;
        StoreVersion = storeVersion;
        PackageReference = packageReference;
        ArtifactDigest = artifactDigest;
        ArtifactSizeBytes = artifactSizeBytes;
        Signature = signature;
        SigningKeyId = signingKeyId;
        ReleaseNotes = releaseNotes;
        Status = StoreImageStatus.Draft;
    }

    public static StoreImage Create(
        Guid id,
        DateTimeOffset createdAt,
        string version,
        string storeVersion,
        string packageReference,
        string artifactDigest,
        long artifactSizeBytes,
        string signature,
        string signingKeyId,
        string? releaseNotes)
    {
        // Both versions must be semantic: the image version orders releases, and
        // the store version is compared against Feature compatibility ranges. A
        // build tag that cannot be parsed would make every range check on that
        // store silently unanswerable.
        RequireSemantic(version, "image version");
        RequireSemantic(storeVersion, "store version");

        return new StoreImage(
            id,
            createdAt,
            version.Trim(),
            storeVersion.Trim(),
            RequireText(packageReference, "package reference", 500),
            RequireDigest(artifactDigest),
            RequirePositiveSize(artifactSizeBytes),
            RequireText(signature, "signature", 1000),
            RequireText(signingKeyId, "signing key id", 100),
            string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes.Trim());
    }

    /// <summary>Makes the image usable for provisioning new stores.</summary>
    public void Publish(Guid publishedBy, DateTimeOffset now)
    {
        if (Status is not StoreImageStatus.Draft)
        {
            throw DomainException.Conflict($"An image in status '{Status}' cannot be published.");
        }

        if (string.IsNullOrWhiteSpace(Signature) || string.IsNullOrWhiteSpace(SigningKeyId))
        {
            throw DomainException.Conflict("An unsigned image cannot be published.");
        }

        Status = StoreImageStatus.Published;
        PublishedAt = now;
        PublishedBy = publishedBy;
        MarkUpdated(now);
    }

    /// <summary>
    /// Withdraws the image from future provisioning. Stores already built from
    /// it are untouched — they are running shops, and a yank is about what gets
    /// built next, never about what is already serving customers.
    /// </summary>
    public void Yank(string reason, DateTimeOffset now)
    {
        if (Status is not StoreImageStatus.Published)
        {
            throw DomainException.Conflict($"An image in status '{Status}' cannot be yanked.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw DomainException.Validation("A yank must say why.");
        }

        Status = StoreImageStatus.Yanked;
        YankedAt = now;
        YankReason = reason.Trim();
        MarkUpdated(now);
    }

    public bool IsUsable => Status is StoreImageStatus.Published;

    public SemanticVersion SemanticVersion => SemanticVersion.Parse(Version);

    private static void RequireSemantic(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || !SemanticVersion.TryParse(value.Trim(), out _))
        {
            throw DomainException.Validation($"A {what} must be a semantic version.");
        }
    }

    private static string RequireDigest(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            throw DomainException.Validation("An artifact digest is required.");
        }

        var normalized = digest.Trim().ToLowerInvariant();

        if (normalized.Length != DigestLength)
        {
            throw DomainException.Validation("An artifact digest must be a hex-encoded sha-256.");
        }

        return normalized;
    }

    private static long RequirePositiveSize(long sizeBytes) =>
        sizeBytes > 0 ? sizeBytes : throw DomainException.Validation("An artifact size must be positive.");

    private static string RequireText(string value, string what, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw DomainException.Validation($"A {what} is required.");
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw DomainException.Validation($"A {what} may be at most {maxLength} characters.");
    }
}

public enum StoreImageStatus
{
    Draft = 0,
    Published = 1,
    Yanked = 2,
}

/// <summary>Persistence for base store images. Platform-wide: every store resolves against the same set.</summary>
public interface IStoreImageRepository
{
    Task<StoreImage?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<StoreImage?> FindByVersionAsync(string version, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<StoreImage>> ListAsync(CancellationToken cancellationToken);

    Task AddAsync(StoreImage image, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
