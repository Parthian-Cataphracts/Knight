using FeatureRegistry.Domain;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Worker declarations, validated at publish.
///
/// Validated hard, because a worker is code KNIGHT causes a store to run on a
/// timer with nobody watching. A malformed entrypoint is a job that fails
/// silently every hour for as long as the Feature is installed, so it is refused
/// where the author is present to fix it rather than discovered in a log.
///
/// The block was deliberately absent until phase 15. Phase 14 needed a
/// scheduled job for loyalty expiry and shipped it as a store management command
/// instead of declaring a `workers:` key nothing read — a declaration that looks
/// like a guarantee and schedules nothing is worse than no declaration
/// (docs/phase-15-verification.md).
/// </summary>
public sealed class ManifestWorkerTests
{
    private static string Json(string? workers) => $$"""
        {
          "apiVersion": "knight.dev/v1",
          "slug": "loyalty-rewards",
          "version": "1.1.0",
          "name": "Loyalty and Rewards",
          "django": { "app_label": "knight_loyalty", "installed_app": "knight_feature_loyalty_rewards" },
          "compatibility": { "storeVersion": "*", "python": "*", "django": "*" },
          "migrations": { "required": true, "reversible": true, "estimatedDurationSeconds": 5 },
          "install": { "strategy": "package-install", "healthCheck": "knight_feature_loyalty_rewards.checks.health" },
          "uninstall": { "strategy": "disable-then-remove", "dataRetentionDays": 730 }{{(workers is null ? "" : $",\n  \"workers\": {workers}")}}
        }
        """;

    private static FeatureManifest Parse(string? workers)
    {
        Assert.True(
            FeatureManifest.TryParse(Json(workers), out var manifest, out var errors),
            string.Join("; ", errors));

        return manifest!;
    }

    private static IReadOnlyList<ManifestError> Reject(string workers)
    {
        Assert.False(FeatureManifest.TryParse(Json(workers), out _, out var errors));

        return errors;
    }

    [Fact]
    public void AManifestWithNoWorkers_DeclaresNone()
    {
        // Most Features have none, and absence must not be an error.
        Assert.Empty(Parse(null).Workers);
    }

    [Fact]
    public void AWorkerIsReadWithItsNameEntrypointAndSchedule()
    {
        var manifest = Parse("""
            [ { "name": "expire-points",
                "entrypoint": "knight_feature_loyalty_rewards.services.expire_stale",
                "schedule": "daily" } ]
            """);

        var worker = Assert.Single(manifest.Workers);
        Assert.Equal("expire-points", worker.Name);
        Assert.Equal("knight_feature_loyalty_rewards.services.expire_stale", worker.Entrypoint);
        Assert.Equal(WorkerSchedule.Daily, worker.Schedule);
    }

    [Fact]
    public void AnOmittedSchedule_DefaultsToDaily()
    {
        // Daily is the safe default: hourly is the one that costs something if
        // it was not meant, and a Feature that omitted the field did not mean it.
        var manifest = Parse("""
            [ { "name": "sweep", "entrypoint": "pkg.module.sweep" } ]
            """);

        Assert.Equal(WorkerSchedule.Daily, Assert.Single(manifest.Workers).Schedule);
    }

    [Theory]
    [InlineData("hourly", WorkerSchedule.Hourly)]
    [InlineData("Daily", WorkerSchedule.Daily)]
    [InlineData("WEEKLY", WorkerSchedule.Weekly)]
    public void EverySupportedSchedule_IsAcceptedWhateverItsCase(string given, WorkerSchedule expected)
    {
        var manifest = Parse($$"""
            [ { "name": "sweep", "entrypoint": "pkg.module.sweep", "schedule": "{{given}}" } ]
            """);

        Assert.Equal(expected, Assert.Single(manifest.Workers).Schedule);
    }

    [Fact]
    public void ACronExpression_IsRefusedWithTheListOfWhatIsAllowed()
    {
        // A closed list rather than cron, deliberately: a cron string is a
        // parser, a timezone question and a support surface. The refusal has to
        // say what *is* allowed, or the author is guessing.
        var errors = Reject("""
            [ { "name": "sweep", "entrypoint": "pkg.module.sweep", "schedule": "0 3 * * *" } ]
            """);

        var error = Assert.Single(errors);
        Assert.Equal("$.workers[0].schedule", error.Path);
        Assert.Contains("hourly", error.Message);
        Assert.Contains("weekly", error.Message);
    }

    [Fact]
    public void AnEntrypointThatIsNotAPythonPath_IsRefused()
    {
        var errors = Reject("""
            [ { "name": "sweep", "entrypoint": "not a path", "schedule": "daily" } ]
            """);

        Assert.Contains(errors, error => error.Path == "$.workers[0].entrypoint");
    }

    [Fact]
    public void AWorkerWithNoName_IsRefused()
    {
        var errors = Reject("""
            [ { "entrypoint": "pkg.module.sweep", "schedule": "daily" } ]
            """);

        Assert.Contains(errors, error => error.Path == "$.workers[0].name");
    }

    [Fact]
    public void AWorkerWithNoEntrypoint_IsRefused()
    {
        var errors = Reject("""
            [ { "name": "sweep", "schedule": "daily" } ]
            """);

        Assert.Contains(errors, error => error.Path == "$.workers[0].entrypoint");
    }

    [Fact]
    public void TwoWorkersOfOneName_AreRefused()
    {
        // A store records the last run per name, so the second would overwrite
        // the first and one of the two would silently never be seen as due.
        var errors = Reject("""
            [ { "name": "sweep", "entrypoint": "pkg.module.one" },
              { "name": "sweep", "entrypoint": "pkg.module.two" } ]
            """);

        Assert.Contains(errors, error => error.Path == "$.workers[1].name");
    }

    [Fact]
    public void SeveralDistinctWorkers_AreAllKept()
    {
        var manifest = Parse("""
            [ { "name": "hourly-sweep", "entrypoint": "pkg.module.a", "schedule": "hourly" },
              { "name": "nightly-roll", "entrypoint": "pkg.module.b", "schedule": "daily" },
              { "name": "weekly-digest", "entrypoint": "pkg.module.c", "schedule": "weekly" } ]
            """);

        Assert.Equal(3, manifest.Workers.Count);
        Assert.Equal(
            [WorkerSchedule.Hourly, WorkerSchedule.Daily, WorkerSchedule.Weekly],
            manifest.Workers.Select(worker => worker.Schedule));
    }

    [Fact]
    public void EveryProblemInTheBlock_IsReportedAtOnce()
    {
        // The whole manifest reader works this way: an author fixing a package
        // should not have to publish four times to find four mistakes.
        var errors = Reject("""
            [ { "entrypoint": "pkg.module.a" },
              { "name": "b", "entrypoint": "not a path" },
              { "name": "c", "entrypoint": "pkg.module.c", "schedule": "fortnightly" } ]
            """);

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void AWorkerThatIsNotAnObject_IsRefusedRatherThanIgnored()
    {
        var errors = Reject("""["just-a-string"]""");

        Assert.Contains(errors, error => error.Path == "$.workers[0]");
    }
}
