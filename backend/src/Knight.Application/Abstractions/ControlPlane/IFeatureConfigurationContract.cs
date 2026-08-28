namespace Knight.Application.Abstractions.ControlPlane;

/// <summary>
/// What a Feature's manifest says its configuration is.
///
/// Read out of the signed manifest of the version the store has, so that
/// "is this setting real" is answered by the document the author signed rather
/// than by a column somebody could edit.
/// </summary>
/// <param name="Known">
/// False when there is no manifest to read — an installation with no version
/// yet, or a version whose manifest cannot be parsed. Callers treat it as "do
/// not judge", because refusing a configuration on the strength of a manifest
/// that could not be read would block an operator over a fault that is not
/// theirs.
/// </param>
/// <param name="ValueKeys">
/// The settings the manifest declares, as the keys of its <c>defaults</c> block.
/// </param>
/// <param name="Types">
/// The JSON kind each declared setting's default has, so a string sent where a
/// number belongs is refused before it reaches a store.
/// </param>
/// <param name="SecretNames">
/// Every secret name the Feature will read: the ones under
/// <c>configuration.secrets</c>, plus the shared secret an
/// <c>external_service</c> Feature names under <c>service.secret</c> — which is
/// the one KNIGHT itself issues and rotates.
/// </param>
public sealed record FeatureConfigurationContract(
    bool Known,
    IReadOnlySet<string> ValueKeys,
    IReadOnlyDictionary<string, string> Types,
    IReadOnlySet<string> SecretNames)
{
    public static FeatureConfigurationContract Unknown { get; } = new(
        false,
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>Reads that contract for one store's installation of one Feature.</summary>
public interface IFeatureConfigurationContractReader
{
    Task<FeatureConfigurationContract> ForInstallationAsync(
        Guid storeId,
        Guid featureId,
        CancellationToken cancellationToken);
}
