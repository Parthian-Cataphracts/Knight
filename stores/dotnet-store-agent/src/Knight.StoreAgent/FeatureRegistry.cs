using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knight.StoreAgent;

/// <summary>One Feature, as this store has it.</summary>
public sealed record InstalledFeature
{
    public string Slug { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string Module { get; init; } = string.Empty;

    public string? MountType { get; init; }

    public string? MountPrefix { get; init; }

    public string? HealthCheck { get; init; }

    public string? Digest { get; init; }

    /// <summary>
    /// Installed and enabled are separate facts, exactly as they are in KNIGHT.
    ///
    /// A Feature whose entitlement lapsed keeps its code and its data and stops
    /// serving. Collapsing the two into one boolean is how "disable" quietly
    /// becomes "uninstall".
    /// </summary>
    public bool Enabled { get; init; }

    public DateTimeOffset InstalledAt { get; init; }

    public int ConfigVersion { get; init; }
}

/// <summary>
/// What this store has installed, on disk, as JSON.
///
/// A file rather than a table, and for the reason the other two reference stores
/// give: the registry has to be readable by a person during an incident without
/// a database being up. It is versioned so a future shape is recognised rather
/// than misread — a registry read as empty is a store that reinstalls
/// everything and reruns every migration.
/// </summary>
public sealed class FeatureRegistry(string root)
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path = Path.Combine(root, "installed.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Root { get; } = root;

    private sealed record Document
    {
        public int SchemaVersion { get; init; } = FeatureRegistry.SchemaVersion;

        public Dictionary<string, InstalledFeature> Features { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Migrations { get; init; } = new(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, InstalledFeature>> AllAsync(CancellationToken cancellationToken = default)
        => (await ReadAsync(cancellationToken)).Features;

    /// <summary>The slugs this store is actually serving, which is what a heartbeat reports.</summary>
    public async Task<IReadOnlyList<string>> EnabledSlugsAsync(CancellationToken cancellationToken = default)
    {
        var document = await ReadAsync(cancellationToken);

        return [.. document.Features.Values.Where(feature => feature.Enabled).Select(feature => feature.Slug).Order(StringComparer.Ordinal)];
    }

    public async Task<InstalledFeature?> FindAsync(string slug, CancellationToken cancellationToken = default)
        => (await ReadAsync(cancellationToken)).Features.GetValueOrDefault(slug);

    public Task RecordAsync(InstalledFeature feature, CancellationToken cancellationToken = default) =>
        MutateAsync(document => document.Features[feature.Slug] = feature, cancellationToken);

    public Task SetEnabledAsync(string slug, bool enabled, CancellationToken cancellationToken = default) =>
        MutateAsync(
            document =>
            {
                if (document.Features.TryGetValue(slug, out var feature))
                {
                    document.Features[slug] = feature with { Enabled = enabled };
                }
            },
            cancellationToken);

    public Task RemoveAsync(string slug, CancellationToken cancellationToken = default) =>
        MutateAsync(document => document.Features.Remove(slug), cancellationToken);

    /// <summary>What state a Feature's schema is in, keyed on its declared namespace.</summary>
    public Task RecordMigrationAsync(string ns, string state, CancellationToken cancellationToken = default) =>
        MutateAsync(document => document.Migrations[ns] = state, cancellationToken);

    public async Task<string?> MigrationStateAsync(string ns, CancellationToken cancellationToken = default)
        => (await ReadAsync(cancellationToken)).Migrations.GetValueOrDefault(ns);

    private async Task<Document> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Document();
        }

        var text = await File.ReadAllTextAsync(_path, cancellationToken);
        var document = JsonSerializer.Deserialize<Document>(text, Json)
            ?? throw new InvalidOperationException($"The feature registry at {_path} is empty.");

        if (document.SchemaVersion != SchemaVersion)
        {
            // Refused rather than treated as empty. A registry read as "nothing
            // is installed" is a store that reinstalls the fleet.
            throw new InvalidOperationException(
                $"The feature registry at {_path} is version {document.SchemaVersion}, and this store understands {SchemaVersion}.");
        }

        return document;
    }

    private async Task MutateAsync(Action<Document> change, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var document = await ReadAsync(cancellationToken);
            change(document);

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Written to a temporary file and moved into place. A process killed
            // halfway through a write would otherwise leave a truncated registry,
            // which this store refuses to read — turning a bad moment into a
            // store that will not start.
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, Json), cancellationToken);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
