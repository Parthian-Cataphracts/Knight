using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace FeatureRegistry.Domain;

/// <summary>
/// One Feature version's declared dependency on another Feature, denormalised
/// out of the manifest at publish time.
///
/// It duplicates what the manifest document already says, and that is
/// deliberate. The resolver walks this graph on every install, upgrade and
/// uninstall preview; doing that by parsing every manifest in the registry would
/// turn a dependency check into a table scan with a JSON parser in the loop.
/// The manifest remains the source of truth — these rows are written only when a
/// version is created, and a published version can never disagree with them
/// because neither can change afterwards.
///
/// The target is stored by slug rather than by id, because a dependency may name
/// a Feature that has not been registered yet. Resolution reports that as a
/// missing dependency, which is a far better error than a foreign key violation
/// during publish.
/// </summary>
public sealed class FeatureDependency : Entity
{
    public Guid FeatureVersionId { get; private set; }

    /// <summary>The slug of the Feature depended on, already normalised.</summary>
    public string DependsOnSlug { get; private set; }

    /// <summary>The permitted version range, stored as written so errors can quote it.</summary>
    public string VersionRangeExpression { get; private set; }

    private FeatureDependency()
    {
        DependsOnSlug = string.Empty;
        VersionRangeExpression = string.Empty;
    }

    private FeatureDependency(Guid id, Guid featureVersionId, string dependsOnSlug, string versionRangeExpression)
        : base(id)
    {
        FeatureVersionId = featureVersionId;
        DependsOnSlug = dependsOnSlug;
        VersionRangeExpression = versionRangeExpression;
    }

    public static FeatureDependency Create(
        Guid id,
        Guid featureVersionId,
        string dependsOnSlug,
        string versionRangeExpression)
    {
        var slug = FeatureSlug.Normalize(dependsOnSlug);

        if (!VersionRange.TryParse(versionRangeExpression, out var range))
        {
            throw DomainException.Validation($"'{versionRangeExpression}' is not a valid version range.");
        }

        return new FeatureDependency(id, featureVersionId, slug, range.Expression);
    }

    public VersionRange Range => VersionRange.Parse(VersionRangeExpression);
}
