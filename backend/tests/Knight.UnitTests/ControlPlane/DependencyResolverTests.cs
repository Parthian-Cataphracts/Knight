using System.Text.Json;
using FeatureRegistry.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The resolver decides what code reaches a customer's store, so every rule in
/// docs/feature-delivery.md §8 gets a test: diamonds, ranges, cycles, yanked
/// versions, conflicts, downgrades, compatibility and hosting.
///
/// These are release-blocking. A resolver that quietly picks the wrong version
/// is a resolver that ships the wrong migration to a live database.
/// </summary>
public sealed class DependencyResolverTests
{
    // --- Fixture helpers ---------------------------------------------------

    private static FeatureManifest Manifest(
        string slug,
        string version,
        (string Slug, string Range)[]? dependencies = null,
        string? storeVersion = null,
        string? python = null,
        string? django = null)
    {
        var json = new
        {
            apiVersion = FeatureManifest.SupportedApiVersion,
            slug,
            version,
            name = slug,
            django = new { app_label = slug.Replace('-', '_'), installed_app = slug.Replace('-', '_') },
            compatibility = new
            {
                storeVersion = storeVersion ?? "*",
                python = python ?? "*",
                django = django ?? "*",
            },
            dependencies = new
            {
                features = (dependencies ?? [])
                    .Select(dependency => new { slug = dependency.Slug, version = dependency.Range })
                    .ToArray(),
            },
            install = new { strategy = "package-install" },
        };

        Assert.True(
            FeatureManifest.TryParse(JsonSerializer.Serialize(json), out var manifest, out var errors),
            string.Join("; ", errors));

        return manifest;
    }

    private static RegistryFeature Feature(
        string slug,
        (string Version, bool Installable, (string Slug, string Range)[]? Dependencies)[] versions,
        FeatureStatus status = FeatureStatus.Published,
        bool requiresDedicated = false,
        string? storeVersion = null,
        string? python = null,
        string? django = null) =>
        new(
            Guid.CreateVersion7(),
            slug,
            slug,
            status,
            requiresDedicated,
            [.. versions.Select(version => new RegistryVersion(
                Guid.CreateVersion7(),
                SemanticVersion.Parse(version.Version),
                Manifest(slug, version.Version, version.Dependencies, storeVersion, python, django),
                version.Installable))]);

    private static RegistryFeature Simple(string slug, params string[] versions) =>
        Feature(slug, [.. versions.Select(version => (version, true, (( string, string)[]?)null))]);

    private static StoreCompatibilityContext Store(
        string? storeVersion = "5.0.0",
        string? python = "3.12",
        string? django = "5.1",
        bool dedicated = false,
        params (string Slug, string Version)[] installed) =>
        new(
            storeVersion,
            python,
            django,
            dedicated,
            installed.ToDictionary(entry => entry.Slug, entry => SemanticVersion.Parse(entry.Version), StringComparer.Ordinal));

    // --- The straightforward cases ----------------------------------------

    [Fact]
    public void ASingleFeature_ResolvesToItsHighestPublishedVersion()
    {
        var resolver = new DependencyResolver([Simple("analytics", "1.0.0", "1.2.0", "1.9.0")]);

        var result = resolver.Resolve("analytics", VersionRange.Any, Store());

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
        var step = Assert.Single(result.Steps);
        Assert.Equal("1.9.0", step.Version.ToString());
        Assert.Equal(PlanAction.Install, step.Action);
        Assert.True(step.IsRoot);
    }

    [Fact]
    public void APinnedRange_LimitsTheChoice()
    {
        var resolver = new DependencyResolver([Simple("analytics", "1.0.0", "1.2.0", "2.0.0")]);

        var result = resolver.Resolve("analytics", VersionRange.Parse(">=1.0.0,<2.0.0"), Store());

        Assert.True(result.IsSuccessful);
        Assert.Equal("1.2.0", Assert.Single(result.Steps).Version.ToString());
    }

    [Fact]
    public void ADependency_IsInstalledBeforeWhatDependsOnIt()
    {
        var resolver = new DependencyResolver([
            Feature("reports", [("1.0.0", true, [("analytics-core", ">=1.0.0,<2.0.0")])]),
            Simple("analytics-core", "1.0.0", "1.4.0"),
        ]);

        var result = resolver.Resolve("reports", VersionRange.Any, Store());

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
        Assert.Equal(["analytics-core", "reports"], result.Steps.Select(step => step.Slug));
        Assert.Equal("1.4.0", result.Steps[0].Version.ToString());
        Assert.False(result.Steps[0].IsRoot);
        Assert.True(result.Steps[1].IsRoot);
    }

    [Fact]
    public void TransitiveDependencies_AreResolvedAllTheWayDown()
    {
        var resolver = new DependencyResolver([
            Feature("top", [("1.0.0", true, [("middle", ">=1.0.0")])]),
            Feature("middle", [("1.0.0", true, [("bottom", ">=1.0.0")])]),
            Simple("bottom", "1.0.0"),
        ]);

        var result = resolver.Resolve("top", VersionRange.Any, Store());

        Assert.True(result.IsSuccessful);
        Assert.Equal(["bottom", "middle", "top"], result.Steps.Select(step => step.Slug));
    }

    // --- Diamonds ----------------------------------------------------------

    [Fact]
    public void ADiamond_PicksAVersionThatSatisfiesBothBranches()
    {
        // Both branches depend on core; the answer must satisfy the intersection
        // of their ranges, not whichever branch happened to be visited first.
        var resolver = new DependencyResolver([
            Feature("app", [("1.0.0", true, [("left", ">=1.0.0"), ("right", ">=1.0.0")])]),
            Feature("left", [("1.0.0", true, [("core", ">=1.0.0,<2.0.0")])]),
            Feature("right", [("1.0.0", true, [("core", ">=1.2.0,<1.5.0")])]),
            Simple("core", "1.0.0", "1.3.0", "1.4.0", "1.9.0", "2.0.0"),
        ]);

        var result = resolver.Resolve("app", VersionRange.Any, Store());

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));

        var core = Assert.Single(result.Steps, step => step.Slug == "core");
        Assert.Equal("1.4.0", core.Version.ToString());
        Assert.Equal(0, result.Steps.ToList().FindIndex(step => step.Slug == "core"));
    }

    [Fact]
    public void ADiamondWithNoOverlap_IsAConflictNotAGuess()
    {
        var resolver = new DependencyResolver([
            Feature("app", [("1.0.0", true, [("left", ">=1.0.0"), ("right", ">=1.0.0")])]),
            Feature("left", [("1.0.0", true, [("core", ">=1.0.0,<2.0.0")])]),
            Feature("right", [("1.0.0", true, [("core", ">=2.0.0")])]),
            Simple("core", "1.0.0", "2.0.0"),
        ]);

        var result = resolver.Resolve("app", VersionRange.Any, Store());

        Assert.False(result.IsSuccessful);
        var failure = Assert.Single(result.Failures, item => item.Slug == "core");
        Assert.Equal(ResolutionFailureCode.ConflictingConstraints, failure.Code);

        // The message has to name both requirers, or nobody can act on it.
        Assert.Contains("left", failure.Message, StringComparison.Ordinal);
        Assert.Contains("right", failure.Message, StringComparison.Ordinal);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void ADiamondIsOnlyPlannedOnce()
    {
        var resolver = new DependencyResolver([
            Feature("app", [("1.0.0", true, [("left", ">=1.0.0"), ("right", ">=1.0.0")])]),
            Feature("left", [("1.0.0", true, [("core", ">=1.0.0")])]),
            Feature("right", [("1.0.0", true, [("core", ">=1.0.0")])]),
            Simple("core", "1.0.0"),
        ]);

        var result = resolver.Resolve("app", VersionRange.Any, Store());

        Assert.True(result.IsSuccessful);
        Assert.Single(result.Steps, step => step.Slug == "core");
        Assert.Equal(4, result.Steps.Count);
    }

    // --- Cycles ------------------------------------------------------------

    [Fact]
    public void ACycle_IsReportedRatherThanOrderedArbitrarily()
    {
        var resolver = new DependencyResolver([
            Feature("a", [("1.0.0", true, [("b", ">=1.0.0")])]),
            Feature("b", [("1.0.0", true, [("a", ">=1.0.0")])]),
        ]);

        var result = resolver.Resolve("a", VersionRange.Any, Store());

        Assert.False(result.IsSuccessful);
        Assert.Equal(ResolutionFailureCode.DependencyCycle, Assert.Single(result.Failures).Code);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void ALongerCycle_IsAlsoCaught()
    {
        var resolver = new DependencyResolver([
            Feature("a", [("1.0.0", true, [("b", ">=1.0.0")])]),
            Feature("b", [("1.0.0", true, [("c", ">=1.0.0")])]),
            Feature("c", [("1.0.0", true, [("a", ">=1.0.0")])]),
        ]);

        var result = resolver.Resolve("a", VersionRange.Any, Store());

        Assert.False(result.IsSuccessful);
        Assert.Equal(ResolutionFailureCode.DependencyCycle, Assert.Single(result.Failures).Code);
    }

    // --- Registry state ----------------------------------------------------

    [Fact]
    public void AYankedVersion_IsNeverChosen()
    {
        var resolver = new DependencyResolver([
            Feature("analytics", [("1.0.0", true, null), ("2.0.0", false, null)]),
        ]);

        var result = resolver.Resolve("analytics", VersionRange.Any, Store());

        Assert.True(result.IsSuccessful);
        Assert.Equal("1.0.0", Assert.Single(result.Steps).Version.ToString());
    }

    [Fact]
    public void AFeatureWithOnlyYankedVersions_HasNothingToInstall()
    {
        var resolver = new DependencyResolver([
            Feature("analytics", [("1.0.0", false, null)]),
        ]);

        var result = resolver.Resolve("analytics", VersionRange.Any, Store());

        Assert.False(result.IsSuccessful);
        Assert.Equal(ResolutionFailureCode.NoMatchingVersion, Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void AnUnknownDependency_NamesTheSlugAndWhoAskedForIt()
    {
        var resolver = new DependencyResolver([
            Feature("reports", [("1.0.0", true, [("analytics-core", ">=1.0.0")])]),
        ]);

        var result = resolver.Resolve("reports", VersionRange.Any, Store());

        Assert.False(result.IsSuccessful);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ResolutionFailureCode.UnknownFeature, failure.Code);
        Assert.Equal("analytics-core", failure.Slug);
        Assert.Contains("reports", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWithdrawnFeature_CannotBeInstalled()
    {
        var resolver = new DependencyResolver([
            Feature("analytics", [("1.0.0", true, null)], status: FeatureStatus.Withdrawn),
        ]);

        var result = resolver.Resolve("analytics", VersionRange.Any, Store());

        Assert.False(result.IsSuccessful);
        Assert.Equal(ResolutionFailureCode.FeatureWithdrawn, Assert.Single(result.Failures).Code);
    }

    // --- What the store already has ---------------------------------------

    [Fact]
    public void AnInstalledVersionThatStillSatisfiesEverything_IsKept()
    {
        // Installing one Feature must not quietly bump the versions of others.
        var resolver = new DependencyResolver([
            Feature("reports", [("1.0.0", true, [("core", ">=1.0.0")])]),
            Simple("core", "1.0.0", "2.0.0"),
        ]);

        var result = resolver.Resolve("reports", VersionRange.Any, Store(installed: ("core", "1.0.0")));

        Assert.True(result.IsSuccessful);
        var core = Assert.Single(result.Steps, step => step.Slug == "core");
        Assert.Equal("1.0.0", core.Version.ToString());
        Assert.Equal(PlanAction.AlreadySatisfied, core.Action);
        Assert.False(core.IsActionable);
    }

    [Fact]
    public void AnInstalledVersionOutsideTheRange_IsUpgraded()
    {
        var resolver = new DependencyResolver([
            Feature("reports", [("1.0.0", true, [("core", ">=2.0.0")])]),
            Simple("core", "1.0.0", "2.0.0"),
        ]);

        var result = resolver.Resolve("reports", VersionRange.Any, Store(installed: ("core", "1.0.0")));

        Assert.True(result.IsSuccessful);
        var core = Assert.Single(result.Steps, step => step.Slug == "core");
        Assert.Equal(PlanAction.Upgrade, core.Action);
        Assert.Equal("1.0.0", core.InstalledVersion?.ToString());
        Assert.Equal("2.0.0", core.Version.ToString());
    }

    [Fact]
    public void ADowngrade_IsRefusedRatherThanPerformed()
    {
        // Downgrading is how data written by a newer schema stops being readable.
        var resolver = new DependencyResolver([
            Feature("reports", [("1.0.0", true, [("core", ">=1.0.0,<2.0.0")])]),
            Simple("core", "1.0.0", "3.0.0"),
        ]);

        var result = resolver.Resolve("reports", VersionRange.Any, Store(installed: ("core", "3.0.0")));

        Assert.False(result.IsSuccessful);
        Assert.Equal(ResolutionFailureCode.DowngradeRefused, Assert.Single(result.Failures).Code);
    }

    // --- Compatibility -----------------------------------------------------

    [Fact]
    public void AStoreTooOldForTheManifest_IsRefused()
    {
        var resolver = new DependencyResolver([
            Feature("analytics", [("1.0.0", true, null)], storeVersion: ">=6.0.0"),
        ]);

        var result = resolver.Resolve("analytics", VersionRange.Any, Store(storeVersion: "5.0.0"));

        Assert.False(result.IsSuccessful);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ResolutionFailureCode.IncompatibleStore, failure.Code);
        Assert.Contains("5.0.0", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreTooNewForTheManifest_IsAlsoRefused()
    {
        var resolver = new DependencyResolver([
            Feature("analytics", [("1.0.0", true, null)], storeVersion: ">=4.0.0,<5.0.0"),
        ]);

        var result = resolver.Resolve("analytics", VersionRange.Any, Store(storeVersion: "5.2.0"));

        Assert.False(result.IsSuccessful);
        Assert.Equal(ResolutionFailureCode.IncompatibleStore, Assert.Single(result.Failures).Code);
    }

    [Theory]
    [InlineData("3.11", ">=3.12")]
    [InlineData("4.2", ">=5.0,<6.0")]
    public void AWrongRuntime_IsRefused(string reported, string required)
    {
        var isPython = required.Contains("3.", StringComparison.Ordinal);

        var resolver = new DependencyResolver([
            Feature(
                "analytics",
                [("1.0.0", true, null)],
                python: isPython ? required : null,
                django: isPython ? null : required),
        ]);

        var result = resolver.Resolve(
            "analytics",
            VersionRange.Any,
            Store(python: isPython ? reported : "3.12", django: isPython ? "5.1" : reported));

        Assert.False(result.IsSuccessful);
        Assert.Equal(ResolutionFailureCode.IncompatibleStore, Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void AnUnreportedRuntime_IsNotTreatedAsAPass()
    {
        // Silence is not compatibility. A store that never said which Django it
        // runs cannot be certified against a constraint on Django.
        var resolver = new DependencyResolver([
            Feature("analytics", [("1.0.0", true, null)], django: ">=5.0,<6.0"),
        ]);

        var result = resolver.Resolve("analytics", VersionRange.Any, Store(django: null));

        Assert.False(result.IsSuccessful);
        Assert.Equal(ResolutionFailureCode.IncompatibleStore, Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void AnUnreportedRuntime_IsFineWhenTheManifestConstrainsNothing()
    {
        var resolver = new DependencyResolver([Simple("analytics", "1.0.0")]);

        var result = resolver.Resolve(
            "analytics",
            VersionRange.Any,
            Store(storeVersion: null, python: null, django: null));

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
    }

    [Fact]
    public void ADedicatedInfrastructureFeature_IsRefusedOnSharedHosting()
    {
        var resolver = new DependencyResolver([
            Feature("isolated", [("1.0.0", true, null)], requiresDedicated: true),
        ]);

        var refused = resolver.Resolve("isolated", VersionRange.Any, Store(dedicated: false));
        Assert.False(refused.IsSuccessful);
        Assert.Equal(ResolutionFailureCode.DedicatedInfrastructureRequired, Assert.Single(refused.Failures).Code);

        var allowed = resolver.Resolve("isolated", VersionRange.Any, Store(dedicated: true));
        Assert.True(allowed.IsSuccessful, string.Join("; ", allowed.Failures));
    }

    [Fact]
    public void AnAlreadySatisfiedFeature_IsNotReCheckedForCompatibility()
    {
        // The store is demonstrably running it, so re-litigating the constraint
        // would block an unrelated install over a Feature nobody is touching.
        var resolver = new DependencyResolver([
            Feature("legacy", [("1.0.0", true, null)], storeVersion: ">=1.0.0,<2.0.0"),
        ]);

        var result = resolver.Resolve(
            "legacy",
            VersionRange.Any,
            Store(storeVersion: "5.0.0", installed: ("legacy", "1.0.0")));

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
        Assert.Equal(PlanAction.AlreadySatisfied, Assert.Single(result.Steps).Action);
    }

    // --- Several roots at once --------------------------------------------

    [Fact]
    public void SeveralRoots_AreResolvedTogetherSoTheyCannotDisagree()
    {
        var resolver = new DependencyResolver([
            Feature("a", [("1.0.0", true, [("core", ">=1.0.0,<2.0.0")])]),
            Feature("b", [("1.0.0", true, [("core", ">=1.5.0,<2.0.0")])]),
            Simple("core", "1.0.0", "1.6.0", "2.0.0"),
        ]);

        var result = resolver.Resolve([RootRequest.Latest("a"), RootRequest.Latest("b")], Store());

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
        Assert.Equal("1.6.0", Assert.Single(result.Steps, step => step.Slug == "core").Version.ToString());
        Assert.Equal(3, result.Steps.Count);
    }

    [Fact]
    public void NoRoots_IsAnEmptyPlanRatherThanAnError()
    {
        var resolver = new DependencyResolver([Simple("analytics", "1.0.0")]);

        var result = resolver.Resolve([], Store());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.Steps);
    }

    // --- Determinism -------------------------------------------------------

    [Fact]
    public void ThePlanIsTheSameEveryTimeItIsResolved()
    {
        // A plan that reorders itself between the preview and the job is a plan
        // nobody can review.
        var features = new[]
        {
            Feature("app", [("1.0.0", true, [("z", ">=1.0.0"), ("a", ">=1.0.0")])]),
            Simple("z", "1.0.0"),
            Simple("a", "1.0.0"),
        };

        var first = new DependencyResolver(features).Resolve("app", VersionRange.Any, Store());
        var second = new DependencyResolver(features).Resolve("app", VersionRange.Any, Store());

        Assert.Equal(
            first.Steps.Select(step => step.Slug),
            second.Steps.Select(step => step.Slug));
        Assert.Equal(["a", "z", "app"], first.Steps.Select(step => step.Slug));
    }
}
