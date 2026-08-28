using System.Text.Json;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knight.Infrastructure.ControlPlane.Adapters;

/// <summary>
/// Where one store's installation of one Feature actually lives, when that
/// Feature is a service.
///
/// Read out of the **signed manifest** of the version the store has installed,
/// not out of a column beside it. The manifest is what the author signed and
/// what the store took delivery of; a copy of its base URL kept somewhere
/// convenient would be a second answer to "where is this service", and the two
/// would disagree the first time a Feature moved.
///
/// Returns null for anything that is not a service — an in-process Feature, a
/// store that has nothing installed, a version whose manifest has no service
/// block. The callers treat null as "there is no shared secret here", which is
/// exactly right: issuing one would put a credential in a store's configuration
/// that nothing will ever check.
/// </summary>
internal sealed class ServiceEndpointReader : IServiceEndpointReader
{
    private readonly ControlPlaneDbContext _context;
    private readonly ILogger<ServiceEndpointReader> _logger;

    public ServiceEndpointReader(ControlPlaneDbContext context, ILogger<ServiceEndpointReader> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ServiceEndpointDescriptor?> ForInstallationAsync(
        Guid storeId,
        Guid featureId,
        CancellationToken cancellationToken)
    {
        var installation = await _context.FeatureInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.StoreId == storeId && row.FeatureId == featureId,
                cancellationToken);

        if (installation is null)
        {
            return null;
        }

        // The version the store is running, and the version it is being moved
        // to if it is mid-install. A store that has just been given this Feature
        // has a target and no installed version yet, and the credential has to
        // be issued before that install can work.
        var versionId = installation.InstalledVersionId ?? installation.TargetVersionId;

        if (versionId is null)
        {
            return null;
        }

        var manifest = await _context.FeatureVersions
            .AsNoTracking()
            .Where(version => version.Id == versionId)
            .Select(version => version.ManifestJson)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(manifest))
        {
            return null;
        }

        var storeSlug = await _context.Stores
            .AsNoTracking()
            .Where(store => store.Id == storeId)
            .Select(store => store.Slug)
            .FirstOrDefaultAsync(cancellationToken);

        if (storeSlug is null)
        {
            return null;
        }

        return Read(manifest, storeId, featureId, installation.FeatureSlug, storeSlug);
    }

    /// <summary>
    /// The two facts, out of the manifest document.
    ///
    /// Parsed rather than deserialised into the registry's own record, because
    /// this is infrastructure reading a stored document and it needs a base URL
    /// and a name. Anything that cannot be read is reported as "not a service"
    /// rather than thrown: a manifest that got here was validated at publish, so
    /// a surprise in it is a reason to leave the credential alone rather than to
    /// fail an entitlement change.
    /// </summary>
    private ServiceEndpointDescriptor? Read(
        string manifestJson,
        Guid storeId,
        Guid featureId,
        string featureSlug,
        string storeSlug)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("architecture", out var architecture)
                || architecture.ValueKind is not JsonValueKind.String
                || !string.Equals(architecture.GetString(), "external_service", StringComparison.Ordinal))
            {
                return null;
            }

            if (!root.TryGetProperty("service", out var service) || service.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            var baseUrl = service.TryGetProperty("base_url", out var url) ? url.GetString() : null;

            if (string.IsNullOrWhiteSpace(baseUrl)
                || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
            {
                return null;
            }

            var secretName = service.TryGetProperty("secret", out var secret) ? secret.GetString() : null;

            if (string.IsNullOrWhiteSpace(secretName))
            {
                return null;
            }

            return new ServiceEndpointDescriptor(storeId, featureId, featureSlug, storeSlug, parsed, secretName);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "The stored manifest for '{Feature}' could not be read, so no service endpoint was resolved.",
                featureSlug);

            return null;
        }
    }
}
