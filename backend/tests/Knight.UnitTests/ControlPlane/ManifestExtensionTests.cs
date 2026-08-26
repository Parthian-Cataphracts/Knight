using FeatureRegistry.Domain;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Database extensions, validated at publish.
///
/// The block exists because three Features wanted one and none could have it. A
/// `CREATE EXTENSION` is not a change to a Feature's own tables — it is a change
/// to the database the store and every other Feature share — so it could be
/// neither a Class A migration nor an honest irreversible one, and it waited
/// three phases behind a rule written for dropped columns
/// (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
///
/// The list is closed, and that is the part worth testing hardest. A PostgreSQL
/// extension can be a procedural language or a foreign-data wrapper, and a
/// Feature able to name one of those in its manifest would be a Feature able to
/// run arbitrary code on every store that installed it.
/// </summary>
public sealed class ManifestExtensionTests
{
    private static string Json(string? extensions, string database = "postgresql") => $$"""
        {
          "apiVersion": "knight.dev/v1",
          "slug": "advanced-search",
          "version": "1.1.0",
          "name": "Advanced Search",
          "django": { "app_label": "knight_search", "installed_app": "knight_feature_advanced_search" },
          "compatibility": {
            "storeVersion": "*", "python": "*", "django": "*"{{(database is null ? "" : $", \"database\": \"{database}\"")}}
          },
          "migrations": {
            "required": true,
            "reversible": true,
            "estimatedDurationSeconds": 10{{(extensions is null ? "" : $",\n    \"extensions\": {extensions}")}}
          },
          "install": { "strategy": "package-install", "healthCheck": "knight_feature_advanced_search.checks.health" },
          "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 0 }
        }
        """;

    private static FeatureManifest Parse(string? extensions, string database = "postgresql")
    {
        Assert.True(
            FeatureManifest.TryParse(Json(extensions, database), out var manifest, out var errors),
            string.Join("; ", errors));

        return manifest!;
    }

    private static IReadOnlyList<ManifestError> Reject(string? extensions, string database = "postgresql")
    {
        Assert.False(FeatureManifest.TryParse(Json(extensions, database), out _, out var errors));

        return errors;
    }

    [Fact]
    public void AManifestWithNoExtensions_DeclaresNone()
    {
        // Most Features need none, and absence must not be an error.
        Assert.Empty(Parse(null).Migrations.Extensions);
    }

    [Fact]
    public void AnAllowedExtension_IsRead()
    {
        var manifest = Parse("""["pg_trgm"]""");

        Assert.Equal(["pg_trgm"], manifest.Migrations.Extensions);
    }

    [Fact]
    public void SeveralAllowedExtensions_AreAllKept()
    {
        var manifest = Parse("""["pg_trgm", "btree_gist"]""");

        Assert.Equal(["pg_trgm", "btree_gist"], manifest.Migrations.Extensions);
    }

    [Fact]
    public void DeclaringAnExtension_DoesNotMakeTheMigrationIrreversible()
    {
        // The practical payoff of the whole decision. The extension is not in the
        // Feature's migration, so everything that *is* in it still reverses, and
        // advanced-search 1.1.0 can add a trigram index and stay Class A.
        Assert.True(Parse("""["pg_trgm"]""").Migrations.Reversible);
    }

    [Theory]
    [InlineData("plpython3u")]
    [InlineData("plperlu")]
    [InlineData("file_fdw")]
    [InlineData("dblink")]
    public void AnExtensionThatCanRunArbitraryCode_IsRefused(string extension)
    {
        // The reason the list is closed rather than validated for shape. Each of
        // these is a way to execute code as the database owner on every store the
        // Feature reaches.
        var errors = Reject($"""["{extension}"]""");

        var error = Assert.Single(errors);
        Assert.Equal("$.migrations.extensions[0]", error.Path);
        Assert.Contains("pg_trgm", error.Message);
    }

    [Fact]
    public void AnUnknownExtension_IsRefusedWithTheListOfWhatIsAllowed()
    {
        // Not hostile, just not vetted. The refusal still has to say what is
        // allowed or the author is guessing at a closed list.
        var errors = Reject("""["postgis"]""");

        Assert.Contains(errors, error => error.Message.Contains("btree_gist"));
    }

    [Fact]
    public void OneExtensionDeclaredTwice_IsRefused()
    {
        var errors = Reject("""["pg_trgm", "pg_trgm"]""");

        Assert.Contains(errors, error => error.Path == "$.migrations.extensions[1]");
    }

    [Fact]
    public void AnExtensionWithoutADeclaredPostgresRequirement_IsRefused()
    {
        // The failure this prevents is specific: the Feature installs onto a
        // store running SQLite, the extension step fails or the migration does,
        // and the customer learns about an engine requirement from a stack trace.
        // compatibility.database exists to refuse that before an install
        // (docs/phase-14-verification.md); declaring an extension is a second,
        // unmistakable statement of the same requirement.
        var errors = Reject("""["pg_trgm"]""", database: "sqlite");

        var error = Assert.Single(errors);
        Assert.Equal("$.compatibility.database", error.Path);
        Assert.Contains("postgresql", error.Message);
    }

    [Fact]
    public void AnExtensionWithNoDatabaseDeclaredAtAll_IsRefused()
    {
        var errors = Reject("""["pg_trgm"]""", database: null!);

        Assert.Contains(errors, error => error.Path == "$.compatibility.database");
    }

    [Fact]
    public void ExtensionsThatAreNotAnArray_AreRefusedRatherThanIgnored()
    {
        var errors = Reject("\"pg_trgm\"");

        Assert.Contains(errors, error => error.Path == "$.migrations.extensions");
    }

    [Fact]
    public void AnEmptyExtensionName_IsRefused()
    {
        var errors = Reject("""[""]""");

        Assert.Contains(errors, error => error.Path.StartsWith("$.migrations.extensions"));
    }

    [Fact]
    public void EveryProblemInTheBlock_IsReportedAtOnce()
    {
        // As everywhere else in the reader: an author fixing a manifest should
        // not have to publish three times to find three mistakes.
        var errors = Reject("""["pg_trgm", "postgis", "plpython3u"]""", database: "mysql");

        Assert.Equal(3, errors.Count);
    }
}
