using System.Text.Json;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knight.Infrastructure.ControlPlane.Adapters;

/// <summary>
/// What a Feature's manifest declares about its own configuration.
///
/// The same shape as <see cref="ServiceEndpointReader"/> and for the same
/// reason: the manifest is what the author signed and what the store took
/// delivery of, so it is the only honest answer to "is this setting one this
/// Feature reads". A copy kept anywhere else would drift from it the first time
/// a Feature added a setting.
/// </summary>
internal sealed class FeatureConfigurationContractReader : IFeatureConfigurationContractReader
{
    private readonly ControlPlaneDbContext _context;
    private readonly ILogger<FeatureConfigurationContractReader> _logger;

    public FeatureConfigurationContractReader(
        ControlPlaneDbContext context,
        ILogger<FeatureConfigurationContractReader> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<FeatureConfigurationContract> ForInstallationAsync(
        Guid storeId,
        Guid featureId,
        CancellationToken cancellationToken)
    {
        var installation = await _context.FeatureInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.StoreId == storeId && row.FeatureId == featureId, cancellationToken);

        // The version it runs, or the one it is being moved to: configuration is
        // often set while an install is still in flight, and refusing it then
        // would make the ordinary sequence the awkward one.
        var versionId = installation?.InstalledVersionId ?? installation?.TargetVersionId;

        if (versionId is null)
        {
            return FeatureConfigurationContract.Unknown;
        }

        var manifest = await _context.FeatureVersions
            .AsNoTracking()
            .Where(version => version.Id == versionId)
            .Select(version => version.ManifestJson)
            .FirstOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(manifest)
            ? FeatureConfigurationContract.Unknown
            : Read(manifest, installation!.FeatureSlug);
    }

    private FeatureConfigurationContract Read(string manifestJson, string slug)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            var root = document.RootElement;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            var secrets = new HashSet<string>(StringComparer.Ordinal);

            if (root.TryGetProperty("configuration", out var configuration)
                && configuration.ValueKind is JsonValueKind.Object)
            {
                if (configuration.TryGetProperty("defaults", out var defaults)
                    && defaults.ValueKind is JsonValueKind.Object)
                {
                    foreach (var setting in defaults.EnumerateObject())
                    {
                        keys.Add(setting.Name);
                        types[setting.Name] = setting.Value.ValueKind.ToString();
                    }
                }

                if (configuration.TryGetProperty("secrets", out var declared)
                    && declared.ValueKind is JsonValueKind.Array)
                {
                    foreach (var secret in declared.EnumerateArray())
                    {
                        if (secret.ValueKind is JsonValueKind.String && secret.GetString() is { Length: > 0 } name)
                        {
                            secrets.Add(name);
                        }
                    }
                }
            }

            // The shared secret an external Feature signs with is named under
            // `service:`, not under `configuration:`, and KNIGHT issues it
            // itself. Leaving it out here would make phase 24's own delivery
            // path fail validation.
            if (root.TryGetProperty("service", out var service)
                && service.ValueKind is JsonValueKind.Object
                && service.TryGetProperty("secret", out var shared)
                && shared.ValueKind is JsonValueKind.String
                && shared.GetString() is { Length: > 0 } sharedName)
            {
                secrets.Add(sharedName);
            }

            return new FeatureConfigurationContract(true, keys, types, secrets);
        }
        catch (JsonException exception)
        {
            // Unknown rather than empty. An unreadable manifest must not become
            // "this Feature declares nothing", which would refuse every setting
            // an operator tried to save.
            _logger.LogWarning(exception, "The stored manifest for '{Feature}' could not be read.", slug);

            return FeatureConfigurationContract.Unknown;
        }
    }
}
