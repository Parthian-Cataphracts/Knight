using System.Text.Json;

namespace FeatureRegistry.Domain;

/// <summary>
/// Turns manifest JSON into a <see cref="FeatureManifest"/>, collecting every
/// problem rather than throwing on the first.
///
/// Collecting is the point. A publish is a batch operation run from a pipeline,
/// and an author who has to fix one field per failed run learns to hate the
/// registry. Every error carries the JSON path of the field it is about, so the
/// dashboard's manifest validator can point at the line.
///
/// The reader is intentionally hand-written rather than driven by a serializer.
/// Manifest fields are not symmetrical with the .NET type — version ranges are
/// strings that must parse, enums are kebab-case, defaults are an arbitrary
/// document — and every one of those conversions is a place a validation message
/// is owed to the author.
/// </summary>
internal sealed class ManifestReader
{
    private readonly List<ManifestError> _errors = [];

    public IReadOnlyList<ManifestError> Errors => _errors;

    public FeatureManifest? Read(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            Fail("$", "The manifest must be a JSON object.");
            return null;
        }

        var apiVersion = RequireString(root, "apiVersion");
        if (apiVersion is not null && apiVersion != FeatureManifest.SupportedApiVersion)
        {
            Fail(
                "$.apiVersion",
                $"Unsupported manifest API version '{apiVersion}'. This registry understands '{FeatureManifest.SupportedApiVersion}'.");
        }

        var slug = RequireString(root, "slug");
        if (slug is not null && !FeatureSlug.IsValid(slug))
        {
            Fail("$.slug", $"'{slug}' is not a valid feature slug.");
            slug = null;
        }

        var version = RequireVersion(root, "version");
        var name = RequireString(root, "name");
        var description = OptionalString(root, "description");

        var django = ReadDjango(root);
        var compatibility = ReadCompatibility(root);
        var dependencies = ReadDependencies(root);
        var migrations = ReadMigrations(root);
        var configuration = ReadConfiguration(root);
        var install = ReadInstall(root);
        var uninstall = ReadUninstall(root);

        if (_errors.Count > 0)
        {
            return null;
        }

        return new FeatureManifest(
            apiVersion!,
            FeatureSlug.Normalize(slug!),
            version!,
            name!,
            description,
            django!,
            compatibility,
            dependencies,
            migrations,
            configuration,
            install!,
            uninstall);
    }

    private DjangoIntegration? ReadDjango(JsonElement root)
    {
        if (!TryGetObject(root, "django", out var django))
        {
            Fail("$.django", "A Feature must say how it attaches to the store's Django project.");
            return null;
        }

        var appLabel = RequireString(django, "app_label", "$.django.app_label");
        var installedApp = RequireString(django, "installed_app", "$.django.installed_app");

        // Both are Python identifiers that end up in INSTALLED_APPS and in a
        // migration table. A malformed one would not fail until the installer is
        // already halfway through a store's database.
        if (appLabel is not null && !IsPythonIdentifier(appLabel))
        {
            Fail("$.django.app_label", $"'{appLabel}' is not a valid Django app label.");
        }

        if (installedApp is not null && !IsDottedPythonPath(installedApp))
        {
            Fail("$.django.installed_app", $"'{installedApp}' is not a valid Python module path.");
        }

        string? include = null;
        string? prefix = null;

        if (TryGetObject(django, "urls", out var urls))
        {
            include = RequireString(urls, "include", "$.django.urls.include");
            prefix = OptionalString(urls, "prefix");

            if (include is not null && !IsDottedPythonPath(include))
            {
                Fail("$.django.urls.include", $"'{include}' is not a valid Python module path.");
            }
        }

        return _errors.Count > 0 ? null : new DjangoIntegration(appLabel!, installedApp!, include, prefix);
    }

    private CompatibilityConstraints ReadCompatibility(JsonElement root)
    {
        if (!TryGetObject(root, "compatibility", out var compatibility))
        {
            // Absent compatibility is not an error but it is a strong claim, and
            // the resolver treats an unbounded range as "the author asserts this
            // runs anywhere". Recording it as Any keeps that claim explicit.
            return new CompatibilityConstraints(VersionRange.Any, VersionRange.Any, VersionRange.Any);
        }

        return new CompatibilityConstraints(
            OptionalRange(compatibility, "storeVersion", "$.compatibility.storeVersion"),
            OptionalRange(compatibility, "python", "$.compatibility.python"),
            OptionalRange(compatibility, "django", "$.compatibility.django"));
    }

    private ManifestDependencies ReadDependencies(JsonElement root)
    {
        if (!TryGetObject(root, "dependencies", out var dependencies))
        {
            return ManifestDependencies.None;
        }

        var features = new List<FeatureDependencyDeclaration>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (dependencies.TryGetProperty("features", out var featureList))
        {
            if (featureList.ValueKind is not JsonValueKind.Array)
            {
                Fail("$.dependencies.features", "Feature dependencies must be an array.");
            }
            else
            {
                var index = 0;
                foreach (var element in featureList.EnumerateArray())
                {
                    var path = $"$.dependencies.features[{index}]";
                    index++;

                    if (element.ValueKind is not JsonValueKind.Object)
                    {
                        Fail(path, "A feature dependency must be an object with a slug and a version range.");
                        continue;
                    }

                    var slug = RequireString(element, "slug", $"{path}.slug");
                    if (slug is null)
                    {
                        continue;
                    }

                    if (!FeatureSlug.IsValid(slug))
                    {
                        Fail($"{path}.slug", $"'{slug}' is not a valid feature slug.");
                        continue;
                    }

                    var normalized = FeatureSlug.Normalize(slug);

                    // Two entries for the same slug are a contradiction the
                    // resolver cannot arbitrate, so they are refused here rather
                    // than silently last-one-wins.
                    if (!seen.Add(normalized))
                    {
                        Fail($"{path}.slug", $"'{normalized}' is declared as a dependency more than once.");
                        continue;
                    }

                    features.Add(new FeatureDependencyDeclaration(
                        normalized,
                        OptionalRange(element, "version", $"{path}.version")));
                }
            }
        }

        var pythonPackages = ReadStringArray(dependencies, "python", "$.dependencies.python");

        return new ManifestDependencies(features, pythonPackages);
    }

    private MigrationPolicy ReadMigrations(JsonElement root)
    {
        if (!TryGetObject(root, "migrations", out var migrations))
        {
            return MigrationPolicy.None;
        }

        var required = OptionalBool(migrations, "required", "$.migrations.required") ?? false;

        // Reversible defaults to false when migrations are required. The safe
        // default is the pessimistic one: assuming an undeclared migration can
        // be undone is how a rollback corrupts a customer's data
        // (docs/adr/0016).
        var reversible = OptionalBool(migrations, "reversible", "$.migrations.reversible") ?? !required;

        var duration = OptionalInt(migrations, "estimatedDurationSeconds", "$.migrations.estimatedDurationSeconds") ?? 0;
        if (duration < 0)
        {
            Fail("$.migrations.estimatedDurationSeconds", "An estimated duration cannot be negative.");
        }

        var maintenance = OptionalBool(migrations, "requiresMaintenanceWindow", "$.migrations.requiresMaintenanceWindow") ?? false;

        return new MigrationPolicy(required, reversible, Math.Max(duration, 0), maintenance);
    }

    private ConfigurationContract ReadConfiguration(JsonElement root)
    {
        if (!TryGetObject(root, "configuration", out var configuration))
        {
            return ConfigurationContract.None;
        }

        var schemaPath = OptionalString(configuration, "schema");

        JsonElement? defaults = null;
        if (configuration.TryGetProperty("defaults", out var defaultsElement))
        {
            if (defaultsElement.ValueKind is not JsonValueKind.Object)
            {
                Fail("$.configuration.defaults", "Configuration defaults must be an object.");
            }
            else
            {
                defaults = defaultsElement.Clone();
            }
        }

        var secrets = ReadStringArray(configuration, "secrets", "$.configuration.secrets");

        // A secret that is also a default would put its value in the package.
        // The manifest is refused rather than the value quietly dropped.
        if (defaults is { } document)
        {
            foreach (var secret in secrets)
            {
                if (document.TryGetProperty(secret, out _))
                {
                    Fail(
                        "$.configuration.defaults",
                        $"'{secret}' is declared as a secret and cannot have a default value in the package.");
                }
            }
        }

        return new ConfigurationContract(schemaPath, defaults, secrets);
    }

    private InstallPolicy? ReadInstall(JsonElement root)
    {
        if (!TryGetObject(root, "install", out var install))
        {
            Fail("$.install", "A Feature must say how it is installed.");
            return null;
        }

        var strategyText = RequireString(install, "strategy", "$.install.strategy");
        var strategy = strategyText switch
        {
            "package-install" => InstallStrategy.PackageInstall,
            "vendored" => InstallStrategy.Vendored,
            "no-op" => InstallStrategy.NoOp,
            null => (InstallStrategy?)null,
            _ => null,
        };

        if (strategyText is not null && strategy is null)
        {
            Fail(
                "$.install.strategy",
                $"'{strategyText}' is not a known install strategy. Expected 'package-install', 'vendored' or 'no-op'.");
        }

        var requiresRestart = OptionalBool(install, "requiresRestart", "$.install.requiresRestart") ?? false;
        var healthCheck = OptionalString(install, "healthCheck");

        if (healthCheck is not null && !IsDottedPythonPath(healthCheck))
        {
            Fail("$.install.healthCheck", $"'{healthCheck}' is not a valid Python callable path.");
        }

        return strategy is null ? null : new InstallPolicy(strategy.Value, requiresRestart, healthCheck);
    }

    private UninstallPolicy ReadUninstall(JsonElement root)
    {
        if (!TryGetObject(root, "uninstall", out var uninstall))
        {
            // The default is the conservative one described in
            // docs/feature-delivery.md §11: disable first, and keep the data
            // long enough that a customer who renews loses nothing.
            return new UninstallPolicy(UninstallStrategy.DisableThenRemove, 30);
        }

        var strategyText = OptionalString(uninstall, "strategy");
        var strategy = strategyText switch
        {
            null or "disable-then-remove" => UninstallStrategy.DisableThenRemove,
            "remove-immediately" => UninstallStrategy.RemoveImmediately,
            _ => (UninstallStrategy?)null,
        };

        if (strategy is null)
        {
            Fail(
                "$.uninstall.strategy",
                $"'{strategyText}' is not a known uninstall strategy. Expected 'disable-then-remove' or 'remove-immediately'.");
            strategy = UninstallStrategy.DisableThenRemove;
        }

        var retention = OptionalInt(uninstall, "dataRetentionDays", "$.uninstall.dataRetentionDays") ?? 30;
        if (retention < 0)
        {
            Fail("$.uninstall.dataRetentionDays", "A retention window cannot be negative.");
            retention = 30;
        }

        return new UninstallPolicy(strategy.Value, retention);
    }

    // --- Primitive readers -------------------------------------------------

    private bool TryGetObject(JsonElement parent, string property, out JsonElement value)
    {
        if (!parent.TryGetProperty(property, out value) || value.ValueKind is JsonValueKind.Null)
        {
            return false;
        }

        if (value.ValueKind is not JsonValueKind.Object)
        {
            Fail($"$.{property}", $"'{property}' must be an object.");
            return false;
        }

        return true;
    }

    private string? RequireString(JsonElement parent, string property, string? path = null)
    {
        path ??= $"$.{property}";

        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            Fail(path, $"'{property}' is required.");
            return null;
        }

        if (value.ValueKind is not JsonValueKind.String)
        {
            Fail(path, $"'{property}' must be a string.");
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            Fail(path, $"'{property}' cannot be empty.");
            return null;
        }

        return text.Trim();
    }

    private string? OptionalString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private SemanticVersion? RequireVersion(JsonElement parent, string property)
    {
        var text = RequireString(parent, property);
        if (text is null)
        {
            return null;
        }

        if (!SemanticVersion.TryParse(text, out var version))
        {
            Fail($"$.{property}", $"'{text}' is not a valid semantic version.");
            return null;
        }

        return version;
    }

    private VersionRange OptionalRange(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return VersionRange.Any;
        }

        if (value.ValueKind is not JsonValueKind.String)
        {
            Fail(path, $"'{property}' must be a version range written as a string.");
            return VersionRange.Any;
        }

        if (!VersionRange.TryParse(value.GetString(), out var range))
        {
            Fail(path, $"'{value.GetString()}' is not a valid version range.");
            return VersionRange.Any;
        }

        return range;
    }

    private bool? OptionalBool(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind is JsonValueKind.False)
        {
            return false;
        }

        Fail(path, $"'{property}' must be true or false.");
        return null;
    }

    private int? OptionalInt(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            Fail(path, $"'{property}' must be a whole number.");
            return null;
        }

        return number;
    }

    private IReadOnlyList<string> ReadStringArray(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind is not JsonValueKind.Array)
        {
            Fail(path, $"'{property}' must be an array of strings.");
            return [];
        }

        var items = new List<string>();
        var index = 0;

        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                Fail($"{path}[{index}]", "Every entry must be a non-empty string.");
            }
            else
            {
                items.Add(element.GetString()!.Trim());
            }

            index++;
        }

        return items;
    }

    private void Fail(string path, string message) => _errors.Add(new ManifestError(path, message));

    private static bool IsPythonIdentifier(string value)
    {
        if (value.Length == 0 || (!char.IsAsciiLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDottedPythonPath(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var segment in value.Split('.'))
        {
            if (!IsPythonIdentifier(segment))
            {
                return false;
            }
        }

        return true;
    }
}
