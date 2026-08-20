namespace Knight.LoadTest;

/// <summary>Minimal `--name value` parsing, shared by both modes.</summary>
internal static class Arguments
{
    public static string Value(string[] args, string name, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return fallback;
    }

    public static int Number(string[] args, string name, int fallback) =>
        int.TryParse(Value(args, name, string.Empty), out var parsed) ? parsed : fallback;

    /// <summary>
    /// Where the seeded credentials are written and read from. Under `artifacts/`
    /// because it holds live store secrets, and that directory is gitignored.
    /// </summary>
    public static string FixturePath(string[] args) =>
        Value(args, "--fixtures", Path.Combine("artifacts", "load-test-fixtures.json"));
}

/// <summary>One store's ingestion credentials, as written by `seed`.</summary>
internal sealed record StoreFixture(string Slug, string ClientId, string ClientSecret, string Environment);
