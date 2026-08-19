using FeatureRegistry.Domain;

namespace FeatureRegistry;

/// <summary>
/// What a publish carries. The artifact itself is not here: it is uploaded to
/// the package store first and named by reference, because KNIGHT does not want
/// a multi-megabyte wheel travelling through its request pipeline and into its
/// database (docs/adr/0015-feature-delivery-mechanism.md).
/// </summary>
public sealed record PublishVersionInput(
    string ManifestJson,
    string PackageReference,
    string ArtifactDigest,
    string Signature,
    string? SigningKeyId,
    string? ReleaseNotes);

public sealed record ManifestValidationResult(
    bool IsValid,
    string? Slug,
    string? Version,
    IReadOnlyList<ManifestError> Errors);

public sealed record FeatureVersionPage(
    IReadOnlyCollection<FeatureVersion> Items,
    int Page,
    int PageSize,
    long TotalCount);

/// <summary>
/// The deployable half of the registry: versions, their artifacts, and the
/// publish and yank decisions.
///
/// Publishing is the security boundary of the whole delivery model. Everything
/// downstream — the resolver, the job, the agent, the store — trusts that a
/// published version's digest and signature were checked once, here, by
/// something that had the public key. So this is where an unverifiable artifact
/// is refused; nowhere later is a good place to discover it.
/// </summary>
public interface IFeatureVersionService
{
    /// <summary>
    /// Validates a manifest without publishing anything. The dashboard's manifest
    /// checker and the packaging tool's pre-flight both use this, so an author
    /// finds out what is wrong before a pipeline run rather than after.
    /// </summary>
    ManifestValidationResult ValidateManifest(string manifestJson);

    Task<FeatureVersionPage> ListAsync(Guid featureId, int page, int pageSize, CancellationToken cancellationToken);

    Task<FeatureVersion?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a new version as a draft, having checked its manifest, its
    /// artifact and its signature.
    /// </summary>
    Task<FeatureVersion> CreateAsync(PublishVersionInput input, CancellationToken cancellationToken);

    /// <summary>Makes a draft version installable.</summary>
    Task<FeatureVersion> PublishAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws a version from future installations. Stores already running it
    /// are deliberately left alone: pulling working code out from under a live
    /// store is a worse outage than the one being fixed.
    /// </summary>
    Task<FeatureVersion> YankAsync(Guid versionId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Yanks every version a signing key ever signed. The containment action for
    /// a compromised key, and the reason the key id is recorded per version
    /// (risks.md R21).
    /// </summary>
    Task<int> YankBySigningKeyAsync(string signingKeyId, string reason, CancellationToken cancellationToken);
}
