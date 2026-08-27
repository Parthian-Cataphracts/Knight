using System.Text.Json;
using FeatureRegistry.Domain;
using Knight.Domain.Versioning;
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
        string? django = null,
        string? database = null,
        string runtime = "django",
        string? node = null,
        string? dotnet = null)
    {
        var compatibility = new Dictionary<string, object?>
        {
            ["storeVersion"] = storeVersion ?? "*",
            ["python"] = python ?? "*",
            ["django"] = django ?? "*",
            ["database"] = database,
            ["node"] = node,
            ["dotnet"] = dotnet,
        };

        var json = new Dictionary<string, object?>
        {
            ["apiVersion"] = FeatureManifest.SupportedApiVersion,
            ["slug"] = slug,
            ["version"] = version,
            ["name"] = slug,
            ["runtime"] = runtime,
            ["compatibility"] = compatibility,
            ["dependencies"] = new
            {
                features = (dependencies ?? [])
                    .Select(dependency => new { slug = dependency.Slug, version = dependency.Range })
                    .ToArray(),
            },
            ["install"] = new { strategy = "package-install" },
        };

        // The integration block a runtime spells its own way (adr/0032 §3).
        if (runtime is "node")
        {
            json["node"] = new
            {
                @namespace = slug.Replace('-', '_'),
                module = $"@knight/{slug}",
                mount = new { export = "router", prefix = $"{slug}/" },
            };
        }
        else if (runtime is "dotnet")
        {
            json["dotnet"] = new
            {
                @namespace = slug.Replace('-', '_'),
                assembly = $"Knight.Feature.{slug.Replace("-", string.Empty)}",
                mount = new { type = $"Knight.Feature.{slug.Replace("-", string.Empty)}.Endpoints", prefix = $"{slug}/" },
            };
        }
        else
        {
            json["django"] = new { app_label = slug.Replace('-', '_'), installed_app = slug.Replace('-', '_') };
        }

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
        string? django = null,
        string? database = null,
        string runtime = "django",
        string? node = null,
        string? dotnet = null) =>
        new(
            Guid.CreateVersion7(),
            slug,
            slug,
            status,
            requiresDedicated,
            [.. versions.Select(version => new RegistryVersion(
                Guid.CreateVersion7(),
                SemanticVersion.Parse(version.Version),
                Manifest(slug, version.Version, version.Dependencies, storeVersion, python, django, database, runtime, node, dotnet),
                version.Installable))]);

    private static RegistryFeature Simple(string slug, params string[] versions) =>
        Feature(slug, [.. versions.Select(version => (version, true, ((string, string)[]?)null))]);

    /// <summary>
    /// A store that has said what it is. `runtime` defaults to django for the
    /// same reason `python` and `django` do: these fixtures are about dependency
    /// resolution, and a store that has reported nothing is refused before any
    /// of that is reached — which is the subject of its own tests below.
    /// </summary>
    private static StoreCompatibilityContext Store(
        string? storeVersion = "5.0.0",
        string? python = "3.12",
        string? django = "5.1",
        bool dedicated = false,
        string? database = "postgresql",
        string? runtime = "django",
        string? runtimeVersion = null,
        params (string Slug, string Version)[] installed) =>
        new(
            storeVersion,
            python,
            django,
            dedicated,
            installed.ToDictionary(entry => entry.Slug, entry => SemanticVersion.Parse(entry.Version), StringComparer.Ordinal),
            database,
            runtime,
            runtimeVersion);

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
    public void AnUpgradeMovesTheFeatureItWasAskedAbout_ToTheNewest()
    {
        // Phase 18 asked a store to upgrade a Feature with no version named and
        // watched nothing happen: the resolver kept the installed version
        // because it still satisfied "*", so the plan came back
        // AlreadySatisfied and queued no job. An upgrade that cannot move
        // forward unless the caller already knows the version number is not an
        // upgrade.
        var resolver = new DependencyResolver([Simple("reviews", "1.0.0", "1.0.1")]);

        var result = resolver.Resolve(
            "reviews",
            VersionRange.Any,
            Store(installed: ("reviews", "1.0.0")),
            moveRootsForward: true);

        Assert.True(result.IsSuccessful);
        var step = Assert.Single(result.Steps);
        Assert.Equal("1.0.1", step.Version.ToString());
        Assert.Equal(PlanAction.Upgrade, step.Action);
    }

    [Fact]
    public void AnInstallDoesNotMoveAnythingForward()
    {
        // The other half of the same rule, and the reason it is a parameter
        // rather than a change of behaviour: asking to install something already
        // installed and satisfying must not turn into an upgrade nobody asked
        // for.
        var resolver = new DependencyResolver([Simple("reviews", "1.0.0", "1.0.1")]);

        var result = resolver.Resolve("reviews", VersionRange.Any, Store(installed: ("reviews", "1.0.0")));

        Assert.Equal(PlanAction.AlreadySatisfied, Assert.Single(result.Steps).Action);
    }

    [Fact]
    public void AnUpgradeDoesNotBumpTheDependenciesItFindsOnTheWay()
    {
        // "An upgrade of one Feature should not quietly bump three others" is
        // the rule the keep-installed preference exists for, and moving the root
        // forward must not weaken it.
        var resolver = new DependencyResolver([
            Feature("reports", [("1.0.0", true, [("core", ">=1.0.0")]), ("1.1.0", true, [("core", ">=1.0.0")])]),
            Simple("core", "1.0.0", "2.0.0"),
        ]);

        var result = resolver.Resolve(
            "reports",
            VersionRange.Any,
            Store(installed: [("reports", "1.0.0"), ("core", "1.0.0")]),
            moveRootsForward: true);

        Assert.True(result.IsSuccessful);
        Assert.Equal("1.1.0", Assert.Single(result.Steps, step => step.Slug == "reports").Version.ToString());

        // Untouched: it satisfies everything asked of it.
        var core = Assert.Single(result.Steps, step => step.Slug == "core");
        Assert.Equal("1.0.0", core.Version.ToString());
        Assert.Equal(PlanAction.AlreadySatisfied, core.Action);
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

    // --- Which runtime the store runs -------------------------------------
    //
    // Added in phase 20. Before it, compatibility was decided entirely on Python
    // and Django versions, so a store that is not Django - and adr/0032 settled
    // that there may be one - failed every check there is and could not be
    // planned against at all. That is the defect phase 18 found for Django
    // stores, and it was still live for the other runtime.

    [Fact]
    public void ADjangoFeature_IsRefusedForANodeStore()
    {
        var resolver = new DependencyResolver([Simple("analytics", "1.0.0")]);

        var result = resolver.Resolve("analytics", VersionRange.Any, Store(runtime: "node", runtimeVersion: "20.11.0"));

        var failure = Assert.Single(result.Failures);
        Assert.Equal(ResolutionFailureCode.RuntimeMismatch, failure.Code);

        // Both sides named. "Incompatible" alone sends an operator looking for a
        // version to bump, and no version will ever fix this one.
        Assert.Contains("django", failure.Message);
        Assert.Contains("node", failure.Message);
    }

    [Fact]
    public void ANodeFeature_IsRefusedForADjangoStore()
    {
        var resolver = new DependencyResolver([Feature("conformance", [("1.0.0", true, null)], runtime: "node")]);

        var result = resolver.Resolve("conformance", VersionRange.Any, Store());

        Assert.Equal(ResolutionFailureCode.RuntimeMismatch, Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void ANodeFeature_InstallsIntoANodeStore()
    {
        var resolver = new DependencyResolver([
            Feature("conformance", [("1.0.0", true, null)], runtime: "node", node: ">=20"),
        ]);

        var result = resolver.Resolve(
            "conformance",
            VersionRange.Any,
            Store(python: null, django: null, runtime: "node", runtimeVersion: "20.11.0"));

        // The whole point: a store with no Python and no Django is a store a
        // Feature can be installed into, as long as it is the right one.
        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
    }

    [Fact]
    public void ANodeStoreIsNotAskedForADjangoVersion()
    {
        var resolver = new DependencyResolver([
            Feature("conformance", [("1.0.0", true, null)], django: ">=5.0", python: ">=3.12", runtime: "node"),
        ]);

        var result = resolver.Resolve(
            "conformance",
            VersionRange.Any,
            Store(python: null, django: null, runtime: "node", runtimeVersion: "20.11.0"));

        // A node Feature that names Django ranges is a manifest mistake, not a
        // reason to refuse the install: the ranges belong to a runtime this
        // Feature does not run on, and checking them would produce two failures
        // about versions the store will never have.
        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
    }

    [Fact]
    public void ANodeStoreTooOldForTheFeature_IsRefused()
    {
        var resolver = new DependencyResolver([
            Feature("conformance", [("1.0.0", true, null)], runtime: "node", node: ">=20"),
        ]);

        var result = resolver.Resolve(
            "conformance",
            VersionRange.Any,
            Store(python: null, django: null, runtime: "node", runtimeVersion: "18.19.0"));

        // The node range is checked, and it is the counterpart of the Django
        // one rather than a decoration: `node-conformance` has declared
        // `node: ">=20"` since phase 17 and nothing read it until now.
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ResolutionFailureCode.IncompatibleStore, failure.Code);
        Assert.Contains("node version", failure.Message);
    }

    [Fact]
    public void AStoreThatHasNotSaidWhatItRuns_IsRefusedRatherThanAssumedToBeDjango()
    {
        var resolver = new DependencyResolver([Simple("analytics", "1.0.0")]);

        var result = resolver.Resolve("analytics", VersionRange.Any, Store(runtime: null));

        // The same rule as every other unreported fact: null is "cannot
        // certify", never "no objection". Assuming Django would have been
        // convenient exactly once - for the stores that existed the day this was
        // written - and wrong every day after that.
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ResolutionFailureCode.IncompatibleStore, failure.Code);
        Assert.Contains("which runtime it runs", failure.Message);
    }

    [Fact]
    public void ARuntimeMismatch_IsReportedOnceRatherThanAsEveryVersionItCannotCheck()
    {
        var resolver = new DependencyResolver([
            Feature("analytics", [("1.0.0", true, null)], python: ">=3.12", django: ">=5.0", storeVersion: ">=1.0.0"),
        ]);

        var result = resolver.Resolve(
            "analytics",
            VersionRange.Any,
            Store(python: null, django: null, runtime: "node", runtimeVersion: "20.11.0"));

        // One failure, about the runtime. Not three, two of which are about
        // Python and Django versions a node store will never have and which
        // nobody can act on.
        Assert.Equal(ResolutionFailureCode.RuntimeMismatch, Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void AThirdRuntime_NeededNoNewMachinery()
    {
        var resolver = new DependencyResolver([
            Feature("storefront-reports", [("1.0.0", true, null)], runtime: "dotnet", dotnet: ">=8.0"),
        ]);

        var result = resolver.Resolve(
            "storefront-reports",
            VersionRange.Any,
            Store(python: null, django: null, runtime: "dotnet", runtimeVersion: "10.0.0"));

        // adr/0032 claimed the delivery path was never Django's. Adding .NET
        // cost the enum one line and the reader one method, and this is the
        // test that says so: nothing in the resolver was taught about .NET
        // beyond which range to compare.
        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
    }

    [Fact]
    public void ADotnetFeature_IsRefusedForANodeStore()
    {
        var resolver = new DependencyResolver([
            Feature("storefront-reports", [("1.0.0", true, null)], runtime: "dotnet"),
        ]);

        var result = resolver.Resolve(
            "storefront-reports",
            VersionRange.Any,
            Store(python: null, django: null, runtime: "node", runtimeVersion: "22.0.0"));

        Assert.Equal(ResolutionFailureCode.RuntimeMismatch, Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void ADotnetStoreTooOldForTheFeature_IsRefused()
    {
        var resolver = new DependencyResolver([
            Feature("storefront-reports", [("1.0.0", true, null)], runtime: "dotnet", dotnet: ">=10.0"),
        ]);

        var result = resolver.Resolve(
            "storefront-reports",
            VersionRange.Any,
            Store(python: null, django: null, runtime: "dotnet", runtimeVersion: "8.0.11"));

        var failure = Assert.Single(result.Failures);
        Assert.Equal(ResolutionFailureCode.IncompatibleStore, failure.Code);
        Assert.Contains(".NET version", failure.Message);
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

    // --- The phase 13 graph, as it actually exists -------------------------
    //
    // The cases above are synthetic on purpose: each isolates one rule. These
    // use the real catalogue, because the rules interacting is what phase 13
    // set out to validate and a fixture that agrees with itself proves nothing
    // about the manifests that were actually written.
    //
    // The graph: reviews-ratings and advanced-search depend on nothing but the
    // base store. customer-segmentation depends on analytics-core >=1.1.0,
    // which is a hard lower bound rather than a preference - 1.0.x has no
    // per-subject aggregation, so a segment cannot be computed at all.

    private static DependencyResolver PhaseThirteenCatalogue() =>
        new(
        [
            Feature("analytics-core", [("1.0.0", true, null), ("1.1.0", true, null)]),
            Feature("analytics-reports", [("1.0.0", true, [("analytics-core", ">=1.0.0,<2.0.0")])]),
            Feature(
                "customer-segmentation",
                [("1.0.0", true, [("analytics-core", ">=1.1.0,<2.0.0")])]),
            Simple("reviews-ratings", "1.0.0"),
            Simple("advanced-search", "1.0.0"),
        ]);

    [Fact]
    public void TheTwoSelfContainedFeatures_ResolveToThemselvesAlone()
    {
        var resolver = PhaseThirteenCatalogue();

        foreach (var slug in new[] { "reviews-ratings", "advanced-search" })
        {
            var result = resolver.Resolve(slug, VersionRange.Any, Store());

            Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
            var step = Assert.Single(result.Steps);
            Assert.Equal(slug, step.Slug);
            Assert.True(step.IsRoot);
        }
    }

    [Fact]
    public void Segmentation_PullsAnalyticsCoreInFirstAndNamesWhoAskedForIt()
    {
        var resolver = PhaseThirteenCatalogue();

        var result = resolver.Resolve("customer-segmentation", VersionRange.Any, Store());

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
        Assert.Equal(
            ["analytics-core", "customer-segmentation"],
            result.Steps.Select(step => step.Slug));

        var dependency = result.Steps[0];
        Assert.False(dependency.IsRoot);
        // Carries the version too - "customer-segmentation 1.0.0" - because the
        // message a refusal produces has to say which release wanted it.
        Assert.StartsWith("customer-segmentation", dependency.RequiredBy);

        // The lower bound decides the version, not "whatever is newest": 1.1.0
        // is the first release that can answer the question this Feature asks.
        Assert.Equal("1.1.0", dependency.Version.ToString());
    }

    [Fact]
    public void AStoreOnAnalyticsOnePointZero_IsUpgradedBeforeSegmentationInstalls()
    {
        // The scenario the phase was built around. Installing segmentation on a
        // store that already has analytics-core 1.0.0 is not a fresh install of
        // one Feature - it is an upgrade of another, sequenced first.
        var resolver = PhaseThirteenCatalogue();

        var result = resolver.Resolve(
            "customer-segmentation",
            VersionRange.Any,
            Store(installed: ("analytics-core", "1.0.0")));

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));

        var upgrade = result.Steps.Single(step => step.Slug == "analytics-core");
        Assert.Equal(PlanAction.Upgrade, upgrade.Action);
        Assert.Equal("1.0.0", upgrade.InstalledVersion?.ToString());
        Assert.Equal("1.1.0", upgrade.Version.ToString());

        // And in that order, because the dependent cannot run against 1.0.0.
        Assert.Equal(0, result.Steps.ToList().FindIndex(step => step.Slug == "analytics-core"));
    }

    [Fact]
    public void AStoreAlreadyOnAnalyticsOnePointOne_OnlyInstallsSegmentation()
    {
        var resolver = PhaseThirteenCatalogue();

        var result = resolver.Resolve(
            "customer-segmentation",
            VersionRange.Any,
            Store(installed: ("analytics-core", "1.1.0")));

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));

        var satisfied = result.Steps.Single(step => step.Slug == "analytics-core");
        Assert.Equal(PlanAction.AlreadySatisfied, satisfied.Action);
        Assert.Equal(
            PlanAction.Install,
            result.Steps.Single(step => step.Slug == "customer-segmentation").Action);
    }

    [Fact]
    public void SegmentationAgainstACatalogueWithoutOnePointOne_IsRefusedRatherThanDowngraded()
    {
        // A range nothing satisfies produces no job and an explanation. The
        // alternative - installing 1.0.0 because it is the only thing there -
        // gives the store a Feature that computes every segment as empty, which
        // a merchant reads as having no customers.
        var resolver = new DependencyResolver(
        [
            Simple("analytics-core", "1.0.0"),
            Feature(
                "customer-segmentation",
                [("1.0.0", true, [("analytics-core", ">=1.1.0,<2.0.0")])]),
        ]);

        var result = resolver.Resolve("customer-segmentation", VersionRange.Any, Store());

        Assert.False(result.IsSuccessful);
        Assert.Empty(result.Steps);
        Assert.Contains(result.Failures, failure => failure.Slug == "analytics-core");
    }

    [Fact]
    public void SegmentationAndReportsTogether_ShareOneAnalyticsCoreThatSuitsBoth()
    {
        // The diamond as the real catalogue contains it: reports accepts
        // >=1.0.0 and segmentation demands >=1.1.0, so the shared dependency
        // has to be 1.1.0 and has to be planned once.
        var resolver = PhaseThirteenCatalogue();

        var result = resolver.Resolve(
            [RootRequest.Latest("customer-segmentation"), RootRequest.Latest("analytics-reports")],
            Store());

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));

        var core = Assert.Single(result.Steps, step => step.Slug == "analytics-core");
        Assert.Equal("1.1.0", core.Version.ToString());
        Assert.Equal(0, result.Steps.ToList().FindIndex(step => step.Slug == "analytics-core"));
    }

    // --- The database a Feature needs --------------------------------------
    //
    // Added in phase 14. `advanced-search` genuinely requires PostgreSQL - its
    // index is a tsvector column and a GIN index - and until the manifest could
    // say so, the install succeeded and the health check failed afterwards,
    // which is a worse way to learn it (docs/phase-13-verification.md).

    private static DependencyResolver SearchNeedingPostgres() =>
        new([Feature("advanced-search", [("1.0.0", true, null)], database: "postgresql")]);

    [Fact]
    public void AFeatureRequiringPostgres_InstallsOnAPostgresStore()
    {
        var result = SearchNeedingPostgres().Resolve(
            "advanced-search", VersionRange.Any, Store(database: "postgresql"));

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
    }

    [Fact]
    public void AFeatureRequiringPostgres_IsRefusedOnAnotherEngine()
    {
        var result = SearchNeedingPostgres().Resolve(
            "advanced-search", VersionRange.Any, Store(database: "mysql"));

        Assert.False(result.IsSuccessful);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ResolutionFailureCode.IncompatibleStore, failure.Code);
        Assert.Contains("mysql", failure.Message);
    }

    [Fact]
    public void AStoreThatHasNotSaidWhichDatabaseItRuns_IsNotTreatedAsAPass()
    {
        // The same rule as an unreported runtime version. Installing because
        // nothing contradicted the requirement is exactly the optimism this
        // check exists to remove.
        var result = SearchNeedingPostgres().Resolve(
            "advanced-search", VersionRange.Any, Store(database: null));

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Failures, f => f.Code == ResolutionFailureCode.IncompatibleStore);
    }

    [Fact]
    public void AFeatureThatDoesNotCareAboutTheDatabase_InstallsAnywhere()
    {
        var resolver = new DependencyResolver([Simple("reviews-ratings", "1.0.0")]);

        foreach (var engine in new string?[] { "postgresql", "mysql", "sqlite", null })
        {
            var result = resolver.Resolve("reviews-ratings", VersionRange.Any, Store(database: engine));

            Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
        }
    }

    [Fact]
    public void TheEngineIsComparedWithoutCaring_AboutCase()
    {
        // A store reporting "PostgreSQL" and a manifest saying "postgresql" are
        // the same engine, and refusing for a capital letter is a failure nobody
        // can read.
        var result = SearchNeedingPostgres().Resolve(
            "advanced-search", VersionRange.Any, Store(database: "PostgreSQL"));

        Assert.True(result.IsSuccessful, string.Join("; ", result.Failures));
    }
}
