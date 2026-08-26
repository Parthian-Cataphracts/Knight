using System.Text.Json;
using Knight.Domain.Versioning;

namespace FeatureRegistry.Domain;

/// <summary>
/// The parsed contents of a <c>knight_manifest.yaml</c>
/// (docs/feature-delivery.md §5).
///
/// The manifest is the contract between a Feature's author and everything that
/// handles the Feature afterwards: KNIGHT validates it at publish time, the
/// resolver reads its dependencies, the compatibility checker reads its
/// constraints, and the store's installer reads its install and migration
/// declarations. Because all four consume the same document, it is parsed into
/// this type exactly once — at publish — and every later reader works from the
/// parsed form rather than re-interpreting free-form JSON.
///
/// Parsing is deliberately strict and total: a manifest either yields a fully
/// valid object or a list of errors naming every field that is wrong. There is
/// no partially valid manifest, because a manifest that is wrong in one field
/// has already told us the author did not test the publish.
/// </summary>
public sealed record FeatureManifest(
    string ApiVersion,
    string Slug,
    SemanticVersion Version,
    string Name,
    string? Description,
    DjangoIntegration Django,
    CompatibilityConstraints Compatibility,
    ManifestDependencies Dependencies,
    MigrationPolicy Migrations,
    ConfigurationContract Configuration,
    InstallPolicy Install,
    UninstallPolicy Uninstall,

    /// <summary>
    /// Scheduled jobs this Feature needs. Empty for most of them.
    ///
    /// Declared here rather than left to each store's cron so that installing a
    /// Feature installs its schedule too. A Feature whose worker has to be wired
    /// up by hand on every store is a Feature that silently does nothing on the
    /// stores where somebody forgot.
    /// </summary>
    IReadOnlyList<WorkerDeclaration> Workers)
{
    /// <summary>
    /// The only manifest API version this build understands. It is checked
    /// explicitly rather than ignored: a future manifest that this KNIGHT cannot
    /// interpret must be refused at publish, not half-understood at install.
    /// </summary>
    public const string SupportedApiVersion = "knight.dev/v1";

    public static bool TryParse(
        JsonElement root,
        out FeatureManifest manifest,
        out IReadOnlyList<ManifestError> errors)
    {
        var reader = new ManifestReader();
        manifest = reader.Read(root)!;
        errors = reader.Errors;

        if (errors.Count > 0)
        {
            manifest = default!;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses a manifest from its JSON text. The store and the publish pipeline
    /// both send JSON; YAML is the authoring format only, converted by the
    /// packaging tool before it ever reaches KNIGHT, so that KNIGHT never has to
    /// carry a YAML parser in its trusted path.
    /// </summary>
    public static bool TryParse(
        string json,
        out FeatureManifest manifest,
        out IReadOnlyList<ManifestError> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParse(document.RootElement.Clone(), out manifest, out errors);
        }
        catch (JsonException exception)
        {
            manifest = default!;
            errors = [new ManifestError("$", $"The manifest is not valid JSON: {exception.Message}")];
            return false;
        }
    }
}

/// <summary>One thing wrong with a manifest, named by its JSON path so the author can find it.</summary>
public sealed record ManifestError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>How the Feature attaches itself to the store's Django project.</summary>
public sealed record DjangoIntegration(
    string AppLabel,
    string InstalledApp,
    string? UrlInclude,
    string? UrlPrefix);

/// <summary>What the Feature requires of the environment it is installed into.</summary>
public sealed record CompatibilityConstraints(
    VersionRange StoreVersion,
    VersionRange Python,
    VersionRange Django,

    /// <summary>
    /// The database engine this Feature requires, or null when it does not care.
    ///
    /// Added because <c>advanced-search</c> genuinely requires PostgreSQL — its
    /// index is a tsvector column and a GIN index — and the schema had nowhere
    /// to say so. A comment in the manifest is not a constraint, and an
    /// undeclared requirement is one the resolver cannot refuse: the install
    /// would succeed and the health check would fail afterwards, which is a
    /// worse way to learn it (docs/phase-13-verification.md).
    ///
    /// A plain string rather than a range: an engine is a name, not a version,
    /// and the version of it that matters is already covered by the store
    /// reporting what it runs.
    /// </summary>
    string? Database = null);

/// <summary>A dependency on another Feature, by slug and permitted version range.</summary>
public sealed record FeatureDependencyDeclaration(string Slug, VersionRange Version);

public sealed record ManifestDependencies(
    IReadOnlyList<FeatureDependencyDeclaration> Features,
    IReadOnlyList<string> PythonPackages)
{
    public static ManifestDependencies None { get; } = new([], []);
}

/// <summary>
/// What the Feature's migrations do and whether they can be undone. Reversibility
/// is a declaration by the author, and KNIGHT treats it as binding: it is the
/// single input that decides whether a failed upgrade can roll the database back
/// or must stop and ask for a human
/// (docs/adr/0016-feature-migration-and-removal-policy.md).
/// </summary>
public sealed record MigrationPolicy(
    bool Required,
    bool Reversible,
    int EstimatedDurationSeconds,
    bool RequiresMaintenanceWindow)
{
    public static MigrationPolicy None { get; } = new(false, true, 0, false);
}

/// <summary>
/// The configuration the Feature accepts. Secrets are named here and only named:
/// their values live encrypted in KNIGHT and travel only over the install
/// channel, so a package that carried one would be a package that leaked it to
/// every customer who received it (docs/feature-delivery.md §9).
/// </summary>
/// <summary>
/// How often a worker runs.
///
/// A closed list rather than a cron expression, deliberately. A cron string is a
/// parser, a timezone question and a support surface, and every scheduled job a
/// Feature has actually wanted so far is one of these three. Widening it later is
/// additive; narrowing it after somebody has shipped a cron string is not.
/// </summary>
public enum WorkerSchedule
{
    Hourly = 0,
    Daily = 1,
    Weekly = 2,
}

/// <summary>
/// A job the Feature needs run on a schedule, without anybody asking.
///
/// The entrypoint is a callable the store imports and calls with no arguments.
/// No arguments on purpose: a worker that took parameters would need somewhere
/// for them to come from, and the only honest source is the Feature's own
/// configuration, which it can read itself.
/// </summary>
public sealed record WorkerDeclaration(
    string Name,
    string Entrypoint,
    WorkerSchedule Schedule);

public sealed record ConfigurationContract(
    string? SchemaPath,
    JsonElement? Defaults,
    IReadOnlyList<string> SecretNames)
{
    public static ConfigurationContract None { get; } = new(null, null, []);
}

public enum InstallStrategy
{
    PackageInstall = 0,
    Vendored = 1,
    NoOp = 2,
}

public enum UninstallStrategy
{
    DisableThenRemove = 0,
    RemoveImmediately = 1,
}

public sealed record InstallPolicy(
    InstallStrategy Strategy,
    bool RequiresRestart,
    string? HealthCheck);

public sealed record UninstallPolicy(
    UninstallStrategy Strategy,
    int DataRetentionDays);
