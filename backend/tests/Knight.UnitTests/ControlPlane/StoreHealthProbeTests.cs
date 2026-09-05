using System.Text.Json;
using Knight.Infrastructure.ControlPlane.Integration;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// What the active health poll captures out of a store's `/health` body.
///
/// The property that matters: a poll must record the runtime block, not only the
/// dependency checks. A store answers `/health` with the two as siblings, and the
/// runtime resolver reads the runtime out of the stored dependency document — so
/// a poll that dropped the runtime left a store it polls but which never
/// heartbeats uncertifiable for delivery. The stored shape must match what a
/// heartbeat leaves, which is the dependency checks with `runtime` merged in.
/// </summary>
public sealed class StoreHealthProbeTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TheRuntimeBlockIsMergedIntoTheStoredDocument()
    {
        var document = StoreHealthProbe.CombineHealthDocument(Root(
            """
            {
              "status": "healthy",
              "dependencies": { "database": { "status": "healthy" } },
              "runtime": { "name": "django", "python": "3.12.10", "django": "5.1.15" }
            }
            """));

        Assert.NotNull(document);
        using var parsed = JsonDocument.Parse(document!);
        var root = parsed.RootElement;

        // The dependency checks survive…
        Assert.True(root.TryGetProperty("database", out _));

        // …and the runtime is merged in under the key the resolver reads, exactly
        // as a heartbeat leaves it.
        Assert.True(root.TryGetProperty("runtime", out var runtime));
        Assert.Equal("django", runtime.GetProperty("name").GetString());
        Assert.Equal("3.12.10", runtime.GetProperty("python").GetString());
    }

    [Fact]
    public void RuntimeIsCapturedEvenWhenTheBodyHasNoDependencyChecks()
    {
        var document = StoreHealthProbe.CombineHealthDocument(Root(
            """{ "status": "healthy", "runtime": { "name": "node", "node": "22.3.0" } }"""));

        Assert.NotNull(document);
        using var parsed = JsonDocument.Parse(document!);
        Assert.Equal("node", parsed.RootElement.GetProperty("runtime").GetProperty("name").GetString());
    }

    [Fact]
    public void ABodyWithNeitherDependenciesNorRuntimeStoresNothing()
    {
        Assert.Null(StoreHealthProbe.CombineHealthDocument(Root("""{ "status": "healthy", "version": "1.0.0" }""")));
    }
}
