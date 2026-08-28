using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knight.StoreAgent;

/// <summary>
/// The configuration KNIGHT delivered for one Feature, on disk.
///
/// Three fields, in the shape every reference store writes:
/// <c>{ "version": 4, "values": {…}, "secrets": {…} }</c>. The .NET agent used
/// to write the values document alone, which meant a Feature's shared secret —
/// the one KNIGHT issues per store and rotates
/// (<c>docs/adr/0034-a-shared-secret-has-a-lifetime.md</c>) — reached this store
/// and was thrown away. A .NET store could take delivery of a service Feature
/// and then be refused by that service, with nothing on either side saying why.
///
/// Read per use, never cached. A rotation is written here while the process is
/// running, and a value captured at start-up would keep a store signing with a
/// secret whose window is closing.
/// </summary>
public static class FeatureConfigurationFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string featureRoot, string slug) =>
        Path.Combine(featureRoot, $"{slug}.config.json");

    /// <summary>
    /// Writes what a job delivered.
    ///
    /// The secrets are written with the values because they are one document to
    /// whoever reads them, and separating them here would mean a Feature having
    /// to know which of two files a given name lives in.
    /// </summary>
    public static async Task WriteAsync(
        string featureRoot,
        string slug,
        int version,
        string valuesJson,
        IReadOnlyDictionary<string, string>? secrets,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(featureRoot);

        var document = new Document
        {
            Version = version,
            Values = Parse(valuesJson),
            Secrets = secrets is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(secrets, StringComparer.Ordinal),
        };

        var path = PathFor(featureRoot, slug);
        var temporary = path + ".tmp";

        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, Json), cancellationToken);
        File.Move(temporary, path, overwrite: true);

        Restrict(path);
    }

    /// <summary>One secret by name, or empty when the store has not been given it.</summary>
    public static string SecretFor(string featureRoot, string slug, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var path = PathFor(featureRoot, slug);

        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Json);

            return document?.Secrets.GetValueOrDefault(name) ?? string.Empty;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // Absent rather than fatal. A configuration that cannot be read is a
            // Feature that has not been given a secret, which the caller already
            // has to handle; a throw here would turn it into a 500 on a
            // shopper's request.
            return string.Empty;
        }
    }

    private static JsonElement Parse(string valuesJson)
    {
        if (string.IsNullOrWhiteSpace(valuesJson))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        try
        {
            return JsonDocument.Parse(valuesJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            // KNIGHT validated this document against the manifest's schema
            // before sending it. If it is not JSON by the time it arrives here,
            // recording it as empty loses less than refusing the install.
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Document
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("values")]
        public JsonElement Values { get; init; }

        [JsonPropertyName("secrets")]
        public Dictionary<string, string> Secrets { get; init; } = new(StringComparer.Ordinal);
    }
}
