using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Identity;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace FeatureRegistry;

/// <summary>What publishing a base store image carries. The artifact itself is uploaded first and named by reference.</summary>
public sealed record PublishStoreImageInput(
    string Version,
    string StoreVersion,
    string PackageReference,
    string ArtifactDigest,
    string Signature,
    string? SigningKeyId,
    string? ReleaseNotes);

/// <summary>
/// The base store image registry.
///
/// Deliberately the same shape as feature version publishing, and for the same
/// reason: the image is executable code that will run a customer's shop. The
/// artifact is confirmed to exist and to hash to the declared digest before the
/// signature is checked, because a signature over a digest nobody verified
/// proves nothing about the bytes a machine will actually be built from.
/// </summary>
public interface IStoreImageService
{
    Task<IReadOnlyCollection<StoreImage>> ListAsync(CancellationToken cancellationToken);

    Task<StoreImage?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<StoreImage> CreateAsync(PublishStoreImageInput input, CancellationToken cancellationToken);

    Task<StoreImage> PublishAsync(Guid imageId, CancellationToken cancellationToken);

    Task<StoreImage> YankAsync(Guid imageId, string reason, CancellationToken cancellationToken);
}

internal sealed class StoreImageService : IStoreImageService
{
    private readonly IStoreImageRepository _images;
    private readonly IFeatureArtifactSigner _signer;
    private readonly IFeatureArtifactStore _artifacts;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUser _currentUser;

    public StoreImageService(
        IStoreImageRepository images,
        IFeatureArtifactSigner signer,
        IFeatureArtifactStore artifacts,
        IAuditTrail audit,
        IDateTimeProvider clock,
        ICurrentUser currentUser)
    {
        _images = images;
        _signer = signer;
        _artifacts = artifacts;
        _audit = audit;
        _clock = clock;
        _currentUser = currentUser;
    }

    public Task<IReadOnlyCollection<StoreImage>> ListAsync(CancellationToken cancellationToken) =>
        _images.ListAsync(cancellationToken);

    public Task<StoreImage?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _images.GetByIdAsync(id, cancellationToken);

    public async Task<StoreImage> CreateAsync(PublishStoreImageInput input, CancellationToken cancellationToken)
    {
        var version = input.Version.Trim();

        if (await _images.FindByVersionAsync(version, cancellationToken) is not null)
        {
            // A published image is immutable. Republishing a version is not an
            // update — it is changing the code somebody's next store gets built
            // from, under a name that already means something else.
            throw new ConflictException(
                $"Base store image {version} already exists. A published image is immutable: publish a new version and yank this one.");
        }

        var artifact = await _artifacts.FindAsync(input.PackageReference, cancellationToken)
            ?? throw Invalid(
                "packageReference",
                $"No artifact was found at '{input.PackageReference}'. Upload the image before publishing it.");

        var declaredDigest = input.ArtifactDigest.Trim().ToLowerInvariant();

        if (!string.Equals(artifact.Digest, declaredDigest, StringComparison.Ordinal))
        {
            throw Invalid(
                "artifactDigest",
                "The declared digest does not match the uploaded artifact. The image may be corrupt or the wrong file.");
        }

        var signingKeyId = string.IsNullOrWhiteSpace(input.SigningKeyId) ? _signer.ActiveKeyId : input.SigningKeyId.Trim();

        if (!_signer.Verify(declaredDigest, input.Signature, signingKeyId))
        {
            throw Invalid("signature", $"The signature over the digest is not valid for signing key '{signingKeyId}'.");
        }

        var image = StoreImage.Create(
            Guid.CreateVersion7(),
            _clock.UtcNow,
            version,
            input.StoreVersion,
            input.PackageReference,
            declaredDigest,
            artifact.SizeBytes,
            input.Signature,
            signingKeyId,
            input.ReleaseNotes);

        await _images.AddAsync(image, cancellationToken);
        await _images.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.image.created",
            nameof(StoreImage),
            image.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { image.Version, image.StoreVersion, image.ArtifactDigest, image.SigningKeyId });

        return image;
    }

    public async Task<StoreImage> PublishAsync(Guid imageId, CancellationToken cancellationToken)
    {
        var image = await RequireAsync(imageId, cancellationToken);

        image.Publish(_currentUser.UserId ?? Guid.Empty, _clock.UtcNow);
        await _images.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.image.published",
            nameof(StoreImage),
            image.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { image.Version, image.StoreVersion, image.ArtifactDigest });

        return image;
    }

    public async Task<StoreImage> YankAsync(Guid imageId, string reason, CancellationToken cancellationToken)
    {
        var image = await RequireAsync(imageId, cancellationToken);

        image.Yank(reason, _clock.UtcNow);
        await _images.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "store.image.yanked",
            nameof(StoreImage),
            image.Id.ToString(),
            null,
            cancellationToken,
            newValue: new { image.Version, reason });

        return image;
    }

    private async Task<StoreImage> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await _images.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Base store image '{id}' was not found.");

    private static ValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}

/// <summary>
/// Reads the published image set from outside the registry. Provisioning needs
/// to check that the image an operator names exists and is usable, without the
/// provisioning module referencing the registry.
/// </summary>
internal sealed class StoreImageCatalog : IBaseImageCatalog
{
    private readonly IStoreImageRepository _images;

    public StoreImageCatalog(IStoreImageRepository images)
    {
        _images = images;
    }

    public async Task<BaseImageDescriptor?> FindUsableAsync(string version, CancellationToken cancellationToken)
    {
        var image = await _images.FindByVersionAsync(version.Trim(), cancellationToken);

        return image is { IsUsable: true }
            ? new BaseImageDescriptor(image.Version, image.StoreVersion, image.ArtifactDigest)
            : null;
    }
}
