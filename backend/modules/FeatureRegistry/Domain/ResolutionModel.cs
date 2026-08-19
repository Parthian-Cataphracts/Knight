namespace FeatureRegistry.Domain;

/// <summary>
/// The registry as the resolver sees it: a Feature and the versions of it that
/// exist. This is a read model rather than the aggregates themselves so that
/// resolution is a pure function of its inputs — the whole point of the
/// dependency and compatibility rules is that they can be exercised
/// exhaustively in unit tests, and that stops being true the moment the
/// algorithm needs a database.
/// </summary>
public sealed record RegistryFeature(
    Guid FeatureId,
    string Slug,
    string Name,
    FeatureStatus Status,
    bool RequiresDedicatedInfrastructure,
    IReadOnlyList<RegistryVersion> Versions);

public sealed record RegistryVersion(
    Guid VersionId,
    SemanticVersion Version,
    FeatureManifest Manifest,
    bool IsInstallable);

/// <summary>
/// What the target store is, as far as compatibility is concerned.
///
/// Everything here is a fact the store *reported*, not a fact KNIGHT assumed.
/// The hosting model is the exception and is deliberately reduced to a single
/// boolean: whether the machine is the customer's alone. The resolver must not
/// depend on the Stores module — modules do not reference their siblings — and
/// it does not need to, because the only question it asks of hosting is that
/// one.
/// </summary>
public sealed record StoreCompatibilityContext(
    string? StoreVersion,
    string? PythonVersion,
    string? DjangoVersion,
    bool HasDedicatedInfrastructure,
    IReadOnlyDictionary<string, SemanticVersion> InstalledFeatures)
{
    public static StoreCompatibilityContext Empty { get; } =
        new(null, null, null, false, new Dictionary<string, SemanticVersion>(StringComparer.Ordinal));
}

/// <summary>What a resolved plan says should happen to one Feature.</summary>
public enum PlanAction
{
    /// <summary>Not present in the store; install it.</summary>
    Install = 0,

    /// <summary>Present at a lower version; upgrade it.</summary>
    Upgrade = 1,

    /// <summary>Already present at exactly the resolved version; nothing to do.</summary>
    AlreadySatisfied = 2,

    /// <summary>
    /// Present at a *higher* version than resolved. Never acted on: silently
    /// downgrading a store to satisfy a dependency is how data written by the
    /// newer schema stops being readable (docs/feature-delivery.md §8).
    /// </summary>
    DowngradeRefused = 3,
}

/// <summary>One step of an install plan, in the order it must be carried out.</summary>
public sealed record PlanStep(
    Guid FeatureId,
    Guid VersionId,
    string Slug,
    string Name,
    SemanticVersion Version,
    SemanticVersion? InstalledVersion,
    PlanAction Action,
    bool IsRoot,
    string RequiredBy)
{
    /// <summary>True when this step actually changes the store.</summary>
    public bool IsActionable => Action is PlanAction.Install or PlanAction.Upgrade;
}

/// <summary>Why a plan could not be produced. The code is what the dashboard branches on; the message is what a human reads.</summary>
public enum ResolutionFailureCode
{
    /// <summary>A dependency names a slug the registry has never heard of.</summary>
    UnknownFeature = 0,

    /// <summary>The Feature exists but has no installable version in the required range.</summary>
    NoMatchingVersion = 1,

    /// <summary>Two requirements for the same Feature cannot both be satisfied.</summary>
    ConflictingConstraints = 2,

    /// <summary>The dependency graph contains a cycle.</summary>
    DependencyCycle = 3,

    /// <summary>The resolved version does not run on this store.</summary>
    IncompatibleStore = 4,

    /// <summary>The Feature needs a dedicated machine and the store is on shared hosting.</summary>
    DedicatedInfrastructureRequired = 5,

    /// <summary>The Feature itself is withdrawn, so nothing may be installed from it.</summary>
    FeatureWithdrawn = 6,

    /// <summary>An already-installed version is newer than what the plan resolved to.</summary>
    DowngradeRefused = 7,
}

public sealed record ResolutionFailure(ResolutionFailureCode Code, string Slug, string Message)
{
    public override string ToString() => $"[{Code}] {Slug}: {Message}";
}

/// <summary>
/// The outcome of resolving one installation request.
///
/// A result is either a plan or a list of reasons there is no plan, never both,
/// and never a plan with warnings that turn out to be fatal. When resolution
/// fails, no job is created and the failures are what the dashboard shows
/// (docs/feature-delivery.md §8).
/// </summary>
public sealed record ResolutionResult(
    IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<ResolutionFailure> Failures)
{
    public bool IsSuccessful => Failures.Count == 0;

    /// <summary>The steps that actually change the store, in install order.</summary>
    public IReadOnlyList<PlanStep> ActionableSteps => [.. Steps.Where(step => step.IsActionable)];

    public static ResolutionResult Failed(params ResolutionFailure[] failures) => new([], failures);

    public static ResolutionResult Failed(IReadOnlyList<ResolutionFailure> failures) => new([], failures);

    public static ResolutionResult Succeeded(IReadOnlyList<PlanStep> steps) => new(steps, []);
}
