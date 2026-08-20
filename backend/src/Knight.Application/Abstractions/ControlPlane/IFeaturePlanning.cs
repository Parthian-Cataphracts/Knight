namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// The delivery engine's view of a resolved install plan.
///
/// Dependency resolution belongs to the registry — it is the registry that knows
/// what versions exist, what they depend on and what they are compatible with.
/// But it is the delivery engine that turns a plan into jobs, and modules do not
/// reference their siblings. So the plan crosses the boundary as these contract
/// types: plain data, no registry types, no manifest.
/// </summary>
public sealed record FeaturePlanStep(
    Guid FeatureId,
    Guid VersionId,
    string Slug,
    string Name,
    string Version,
    string? InstalledVersion,
    FeaturePlanAction Action,
    bool IsRoot,
    string RequiredBy)
{
    public bool IsActionable => Action is FeaturePlanAction.Install or FeaturePlanAction.Upgrade;
}

public enum FeaturePlanAction
{
    Install = 0,
    Upgrade = 1,
    AlreadySatisfied = 2,
    DowngradeRefused = 3,
}

/// <summary>Why there is no plan. The code is what the dashboard branches on; the message is what a person reads.</summary>
public sealed record FeaturePlanFailure(string Code, string Slug, string Message);

/// <summary>
/// A plan, or the reasons there is none — never both. When resolution fails no
/// job is created, and these failures are what the store's installation row
/// records as its blocking reason (docs/feature-delivery.md §8).
/// </summary>
public sealed record FeaturePlan(
    IReadOnlyList<FeaturePlanStep> Steps,
    IReadOnlyList<FeaturePlanFailure> Failures)
{
    public bool IsSuccessful => Failures.Count == 0;

    public IReadOnlyList<FeaturePlanStep> ActionableSteps => [.. Steps.Where(step => step.IsActionable)];

    /// <summary>The failures as one line, for the blocking reason a person will read in the dashboard.</summary>
    public string DescribeFailures() => string.Join(" ", Failures.Select(failure => failure.Message));
}

/// <summary>What the target store is, as far as compatibility is concerned.</summary>
public sealed record FeaturePlanContext(
    Guid StoreId,
    string? StoreVersion,
    string? PythonVersion,
    string? DjangoVersion,
    bool HasDedicatedInfrastructure,
    IReadOnlyDictionary<string, string> InstalledFeatures);

/// <summary>
/// Resolves what would have to happen for a store to end up running a Feature.
/// Implemented by the registry, consumed by delivery.
/// </summary>
public interface IFeaturePlanResolver
{
    /// <summary>
    /// Resolves one Feature against a store.
    /// </summary>
    /// <param name="versionRange">
    /// The versions the caller will accept — usually the plan's pinned range.
    /// Null or "*" means whichever published version is newest.
    /// </param>
    Task<FeaturePlan> ResolveAsync(
        string slug,
        string? versionRange,
        FeaturePlanContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves several Features together. Provisioning installs a whole plan's
    /// worth at once, and resolving them one at a time would let two of them
    /// settle on incompatible versions of a shared dependency.
    /// </summary>
    Task<FeaturePlan> ResolveManyAsync(
        IReadOnlyList<(string Slug, string? VersionRange)> roots,
        FeaturePlanContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads the facts about a store that compatibility depends on. A port rather
/// than a module reference: the delivery engine and the Stores module stay
/// independent of each other.
/// </summary>
public interface IStoreDeliveryReader
{
    Task<FeaturePlanContext?> GetPlanContextAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>The customer that owns the store, or null when there is no such store.</summary>
    Task<Guid?> GetOwningCustomerAsync(Guid storeId, CancellationToken cancellationToken);

    /// <summary>
    /// Every store a rollout of this Feature could target: one that already has
    /// the Feature installed and is not already on the target version.
    ///
    /// Crosses customers, which nothing else in delivery does — a rollout is a
    /// platform operation over the whole fleet. The caller is platform-only for
    /// that reason.
    ///
    /// Stores that do not have the Feature at all are excluded. A rollout moves a
    /// version forward; installing a Feature somewhere for the first time is an
    /// entitlement decision, and folding the two together would let a version
    /// bump quietly install software into stores that never had it.
    /// </summary>
    Task<IReadOnlyCollection<RolloutCandidateStore>> ListRolloutCandidatesAsync(
        string featureSlug,
        string targetVersion,
        CancellationToken cancellationToken);
}

/// <summary>
/// A store a rollout could move onto a new version.
///
/// <paramref name="IsProduction"/> is carried so the canary can be chosen
/// safely: given the choice, the first store to receive an unproven version
/// should not be somebody's live shop.
/// </summary>
public sealed record RolloutCandidateStore(
    Guid FeatureId,
    Guid StoreId,
    Guid CustomerId,
    string StoreName,
    string Environment,
    bool IsProduction,
    string? InstalledVersion);

/// <summary>
/// Signs and verifies feature artifacts.
///
/// An abstraction rather than a concrete algorithm because the custody model is
/// expected to move: file-backed Ed25519 today, a cloud KMS or HSM later, with
/// nothing above this line changing when it does (risks.md R21).
/// </summary>
public interface IFeatureArtifactSigner
{
    /// <summary>The key that signs new artifacts, by id. Recorded on every version so a revoked key can be traced.</summary>
    string ActiveKeyId { get; }

    /// <summary>Signs an artifact digest, returning a base64 detached signature.</summary>
    string Sign(string artifactDigest);

    /// <summary>
    /// Verifies a detached signature against a digest. Called at publish so that
    /// an artifact nobody can verify is refused before it is installable, rather
    /// than discovered by a store halfway through an install.
    /// </summary>
    bool Verify(string artifactDigest, string signature, string keyId);
}

/// <summary>Where a built artifact lives, and how an agent is given temporary access to one.</summary>
public interface IFeatureArtifactStore
{
    /// <summary>True when an object exists at the reference. Publish refuses a version whose artifact is not there.</summary>
    Task<FeatureArtifactMetadata?> FindAsync(string packageReference, CancellationToken cancellationToken);

    /// <summary>
    /// Mints a short-lived URL for one fetch. Minted per job and never stored:
    /// a durable URL to a signed artifact is a credential with no expiry.
    /// </summary>
    Task<Uri> CreateDownloadUrlAsync(string packageReference, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>
    /// Stores an uploaded package and answers what it actually is.
    ///
    /// The digest returned is computed from the stored bytes, never taken from
    /// the uploader: the publish check compares what was uploaded against what
    /// the signature covers, and a digest supplied by the same party as the file
    /// would make that comparison meaningless. Signing itself stays offline —
    /// this accepts an already-signed package and never holds a signing key
    /// (TODO.md phase 9).
    /// </summary>
    Task<FeatureArtifactMetadata> SaveAsync(
        string fileName,
        Stream content,
        CancellationToken cancellationToken);
}

public sealed record FeatureArtifactMetadata(string PackageReference, string Digest, long SizeBytes);
