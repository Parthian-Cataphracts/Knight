using FeatureRegistry.Domain;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Which runtime a Feature is built for, and the wiring that goes with it.
///
/// Everything about Feature delivery was runtime-neutral except the one file
/// that decided whether a Feature could be published at all: the reader refused
/// a manifest with no <c>django:</c> block. That left the project saying a
/// Feature is deployable code and never a flag
/// (<c>adr/0014</c>) while a non-Django store had nothing available to it but a
/// flag it enforced itself. R26 recorded the contradiction and the product owner
/// resolved it in phase 17 (<c>adr/0032</c>).
///
/// What these tests pin is the shape of that resolution rather than the fact of
/// it: the three names are the same whatever the runtime is, only their spelling
/// and their validation rules differ, and a manifest that carries wiring for a
/// runtime it does not declare is an author who copied a file.
/// </summary>
public sealed class ManifestRuntimeTests
{
    private static string Json(string? runtime, string wiring, string? healthCheck = null, string? workers = null) => $$"""
        {
          "apiVersion": "knight.dev/v1",
          "slug": "subscriptions",
          "version": "1.0.0",
          "name": "Subscriptions",
          {{(runtime is null ? "" : $"\"runtime\": \"{runtime}\",")}}
          {{wiring}},
          "compatibility": { "storeVersion": "*", "python": "*", "django": "*" },
          "migrations": { "required": true, "reversible": true, "estimatedDurationSeconds": 5 },
          "install": { "strategy": "package-install", "healthCheck": {{healthCheck ?? "\"knight_feature_subscriptions.checks.health\""}} },
          "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 730 }{{(workers is null ? "" : $",\n  \"workers\": {workers}")}}
        }
        """;

    private const string DjangoWiring = """
        "django": {
            "app_label": "knight_subscriptions",
            "installed_app": "knight_feature_subscriptions",
            "urls": { "include": "knight_feature_subscriptions.urls", "prefix": "subscriptions/" }
          }
        """;

    private const string NodeWiring = """
        "node": {
            "namespace": "knight_subscriptions",
            "module": "@knight/feature-subscriptions",
            "mount": { "export": "router", "prefix": "subscriptions/" }
          }
        """;

    /// <summary>A node Feature spells a callable as a module and an export.</summary>
    private const string NodeHealthCheck = "\"@knight/feature-subscriptions#health\"";

    private static FeatureManifest Parse(string? runtime, string wiring, string? healthCheck = null, string? workers = null)
    {
        Assert.True(
            FeatureManifest.TryParse(Json(runtime, wiring, healthCheck, workers), out var manifest, out var errors),
            string.Join("; ", errors));

        return manifest!;
    }

    private static IReadOnlyList<ManifestError> Reject(string? runtime, string wiring, string? healthCheck = null, string? workers = null)
    {
        Assert.False(FeatureManifest.TryParse(Json(runtime, wiring, healthCheck, workers), out _, out var errors));

        return errors;
    }

    // --- The default ---------------------------------------------------------

    [Fact]
    public void AManifestThatDoesNotSayItsRuntime_IsDjango()
    {
        // Thirteen manifests were written before this field existed and every one
        // of them is a Django Feature. Refusing them would be breaking a
        // published contract in order to add a field whose value they all imply.
        Assert.Equal(FeatureRuntime.Django, Parse(null, DjangoWiring).Runtime.Runtime);
    }

    [Fact]
    public void ARuntimeKnightCannotDeliverTo_IsRefusedByName()
    {
        var errors = Reject("erlang", DjangoWiring);

        Assert.Contains(errors, error => error.Path == "$.runtime");

        // The message lists what it does understand. An author who has typed a
        // runtime KNIGHT does not know needs to be told what it does know, not
        // merely that they were wrong.
        Assert.Contains(errors, error => error.Message.Contains("django") && error.Message.Contains("node"));
    }

    // --- The three names -----------------------------------------------------

    [Fact]
    public void DjangoWiringIsReadIntoTheNeutralNames()
    {
        var runtime = Parse("django", DjangoWiring).Runtime;

        // An app_label is a namespace, an installed_app is a module, and a urls
        // block is a mount. The whole of adr/0032 §3 in four assertions.
        Assert.Equal("knight_subscriptions", runtime.Namespace);
        Assert.Equal("knight_feature_subscriptions", runtime.Module);
        Assert.Equal("knight_feature_subscriptions.urls", runtime.MountExport);
        Assert.Equal("subscriptions/", runtime.MountPrefix);
    }

    [Fact]
    public void NodeWiringIsReadIntoTheSameNeutralNames()
    {
        var runtime = Parse("node", NodeWiring, NodeHealthCheck).Runtime;

        Assert.Equal(FeatureRuntime.Node, runtime.Runtime);
        Assert.Equal("knight_subscriptions", runtime.Namespace);
        Assert.Equal("@knight/feature-subscriptions", runtime.Module);
        Assert.Equal("router", runtime.MountExport);
        Assert.Equal("subscriptions/", runtime.MountPrefix);
    }

    [Fact]
    public void AFeatureThatServesNothing_HasNoMount()
    {
        const string wiring = """
            "node": { "namespace": "knight_jobs", "module": "@knight/feature-jobs" }
            """;

        var runtime = Parse("node", wiring, "\"@knight/feature-jobs#health\"").Runtime;

        // Null rather than a default. A store mounts nothing for a Feature with
        // no router, and inventing a prefix would mount an import error.
        Assert.Null(runtime.MountExport);
        Assert.Null(runtime.MountPrefix);
    }

    // --- The block must match the declaration --------------------------------

    [Fact]
    public void AManifestWithNoBlockForItsRuntime_IsRefused()
    {
        Assert.Contains(Reject("node", DjangoWiring, NodeHealthCheck), error => error.Path == "$.node");
    }

    [Fact]
    public void AManifestCarryingWiringForARuntimeItDoesNotDeclare_IsRefused()
    {
        // An author who has copied a manifest. Cheaper to say so at publish than
        // to deliver a package the store cannot load.
        const string both = $"{NodeWiring}, {DjangoWiring}";

        Assert.Contains(Reject("node", both, NodeHealthCheck), error => error.Path == "$.django");
    }

    // --- Validation is per runtime, because the spellings are ----------------

    [Fact]
    public void ADjangoModuleIsHeldToPythonRules()
    {
        const string wiring = """
            "django": { "app_label": "knight_subscriptions", "installed_app": "@knight/feature-subscriptions" }
            """;

        Assert.Contains(Reject("django", wiring), error => error.Path == "$.django.installed_app");
    }

    [Fact]
    public void ANodeModuleIsHeldToNpmRules()
    {
        const string wiring = """
            "node": { "namespace": "knight_subscriptions", "module": "Knight_Feature_Subscriptions" }
            """;

        // Upper case is not a valid npm name, and this one would have passed the
        // Python rules cleanly — which is the argument for validating per runtime
        // rather than reusing whichever check was written first.
        Assert.Contains(Reject("node", wiring, NodeHealthCheck), error => error.Path == "$.node.module");
    }

    [Fact]
    public void ANodeModuleThatClimbsOutOfItsPackage_IsRefused()
    {
        const string wiring = """
            "node": { "namespace": "knight_subscriptions", "module": "../../etc/passwd" }
            """;

        // A delivered artifact reaching into the store around it. The rule is
        // npm's own, and this is the reason it is enforced rather than assumed.
        Assert.Contains(Reject("node", wiring, NodeHealthCheck), error => error.Path == "$.node.module");
    }

    [Fact]
    public void ANodeCallableIsAModuleAndAnExport()
    {
        var manifest = Parse(
            "node",
            NodeWiring,
            healthCheck: "\"@knight/feature-subscriptions#health\"",
            workers: """[{ "name": "renew", "entrypoint": "@knight/feature-subscriptions#renewDue", "schedule": "daily" }]""");

        Assert.Equal("@knight/feature-subscriptions#health", manifest.Install.HealthCheck);
        Assert.Equal("@knight/feature-subscriptions#renewDue", Assert.Single(manifest.Workers).Entrypoint);
    }

    [Fact]
    public void ANodeCallableWithoutAnExport_IsRefusedWithTheShapeItWanted()
    {
        var errors = Reject("node", NodeWiring, healthCheck: "\"@knight/feature-subscriptions\"");
        var error = Assert.Single(errors, item => item.Path == "$.install.healthCheck");

        // Told what to write, not merely that this was wrong. An author reading
        // "is not a valid Python callable path" about a node Feature would
        // rightly conclude the registry was broken.
        Assert.Contains("module#exportedName", error.Message);
    }

    [Fact]
    public void ADottedPathIsNotANodeCallable()
    {
        // The exact mistake a Django author makes on their first node Feature.
        Assert.Contains(
            Reject("node", NodeWiring, healthCheck: "\"knight_feature_subscriptions.checks.health\""),
            error => error.Path == "$.install.healthCheck");
    }

    [Fact]
    public void ANodeEntrypointIsNotAcceptedForADjangoFeature()
    {
        Assert.Contains(
            Reject("django", DjangoWiring, healthCheck: "\"@knight/feature-subscriptions#health\""),
            error => error.Path == "$.install.healthCheck");
    }
}
