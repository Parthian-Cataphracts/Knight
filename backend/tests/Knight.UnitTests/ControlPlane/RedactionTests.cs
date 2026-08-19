using System.Text.Json;
using Knight.Application.Abstractions.Observability;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The guarantee that no secret reaches a sink KNIGHT writes to
/// (docs/authorization.md §7, docs/security-threat-model.md).
///
/// These are release-blocking. A secret that reaches a log is not recalled by
/// deleting the log — it has already been shipped to a collector, indexed, and
/// included in a backup — so the only useful moment to catch it is here.
/// </summary>
public sealed class RedactionTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("clientSecret")]
    [InlineData("client_secret")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("refreshToken")]
    [InlineData("mfaSecret")]
    [InlineData("privateKey")]
    [InlineData("connectionString")]
    [InlineData("authorization")]
    public void PropertiesThatNameACredentialAreReplaced(string propertyName)
    {
        var document = Redaction.Document(new Dictionary<string, object?>
        {
            [propertyName] = "hunter2-the-actual-secret",
        });

        Assert.DoesNotContain("hunter2", document!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Redaction.Placeholder, document, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacedRatherThanDropped()
    {
        // The record must still say that the value changed, without saying what
        // it changed to. A dropped property is indistinguishable from one that
        // was never set.
        var document = Redaction.Document(new { clientSecret = "s3cret", name = "Ali" });

        using var parsed = JsonDocument.Parse(document!);

        Assert.Equal(Redaction.Placeholder, parsed.RootElement.GetProperty("clientSecret").GetString());
        Assert.Equal("Ali", parsed.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void NestedAndArrayedSecretsAreFound()
    {
        var document = Redaction.Document(new
        {
            store = new { name = "cafe1", credential = new { clientSecret = "leaked-one" } },
            channels = new[] { new { webhook = "https://hooks.example.com", secret = "leaked-two" } },
        });

        Assert.DoesNotContain("leaked-one", document!, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked-two", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretInAValueIsFoundEvenWhenTheKeyIsInnocent()
    {
        // The case that matters most in practice: an agent or a store puts a
        // credential into a field nobody named "secret".
        var document = Redaction.Document(new
        {
            detail = "connecting with Password=hunter2;Host=db",
        });

        Assert.DoesNotContain("hunter2", document!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefgh", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("client_secret=knight-abc123-supersecretvalue", "supersecretvalue")]
    [InlineData("psql postgres://knight:hunter2@db:5432/knight", "hunter2")]
    [InlineData("Host=db;Username=knight;Password=hunter2", "hunter2")]
    [InlineData("{\"token\": \"abcdef123456\"}", "abcdef123456")]
    public void RecognisableSecretsInFreeTextAreRemoved(string text, string secret)
    {
        var redacted = Redaction.Text(text);

        Assert.DoesNotContain(secret, redacted!, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreCredentialIsRecognisedByItsPrefix()
    {
        // KNIGHT's own credentials carry a prefix precisely so they can be spotted
        // here and by a secret scanner.
        var redacted = Redaction.Text("agent failed to authenticate with knight-abcdef-0123456789abcdef");

        Assert.DoesNotContain("0123456789abcdef", redacted!, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        // Redaction that mangles useful output gets switched off, and then it
        // protects nothing at all.
        const string trace = "File \"apps/orders/views.py\", line 142, in create";

        Assert.Equal(trace, Redaction.Text(trace));
    }

    [Fact]
    public void IdentifiersAndHashesSurvive()
    {
        const string text = "installation 9f1c2b7e-1f3a-4a12-9d0e-2f8b7c6d5e4a digest sha256:abc123def456";

        Assert.Equal(text, Redaction.Text(text));
    }

    [Fact]
    public void MalformedJsonIsStillRedactedRatherThanPassedThrough()
    {
        // Job output arrives from an agent and is not guaranteed to be valid
        // JSON. Falling back to returning it untouched would be the one path
        // where a secret gets through.
        var redacted = Redaction.Json("{\"password\": \"hunter2\", broken");

        Assert.DoesNotContain("hunter2", redacted!, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonTextIsRedactedByPropertyName()
    {
        var redacted = Redaction.Json("{\"clientSecret\":\"leaked\",\"slug\":\"analytics-core\"}");

        Assert.DoesNotContain("leaked", redacted!, StringComparison.Ordinal);
        Assert.Contains("analytics-core", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void NullsPassThroughUnchanged()
    {
        Assert.Null(Redaction.Document(null));
        Assert.Null(Redaction.Text(null));
        Assert.Null(Redaction.Json(null));
    }
}
