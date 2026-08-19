using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Knight.Application.Abstractions.Observability;

/// <summary>
/// The one place KNIGHT removes secrets from anything it is about to write down
/// (docs/authorization.md §7, docs/security-threat-model.md).
///
/// It exists centrally rather than per call site because the three sinks that
/// matter — the audit trail, the log stream, and job output returned by agents —
/// have different shapes but exactly one requirement, and a second
/// implementation is a second chance to get it wrong. A secret that reaches a
/// log is not recalled by deleting the log: it has already been shipped,
/// indexed, and backed up.
///
/// Two passes, deliberately:
///
/// * **By name**, for structured documents. Anything whose property name looks
///   like a credential is replaced rather than dropped, so the record still says
///   that the value changed without saying what to.
/// * **By shape**, for free text. Job output and log lines are strings produced
///   outside KNIGHT, where nobody named the field at all — an agent echoing a
///   command line, a Django traceback carrying a connection string. Names cannot
///   help there, so the recognisable shapes are matched instead.
///
/// The shape pass is necessarily incomplete and is not the primary defence: not
/// putting secrets into strings is. It is the net under the trapeze.
/// </summary>
public static class Redaction
{
    public const string Placeholder = "***";

    private static readonly string[] SensitiveFragments =
    [
        "password", "secret", "token", "credential", "apikey", "api_key",
        "signature", "privatekey", "private_key", "clientsecret", "client_secret",
        "authorization", "connectionstring", "connection_string", "mfasecret", "mfa_secret",
        "passphrase", "sessionkey", "session_key", "refreshtoken", "refresh_token",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Value shapes worth catching in free text. Each is anchored on a
    /// distinctive prefix or structure rather than on entropy, because an
    /// entropy heuristic redacts hashes, ids and stack offsets — which makes the
    /// output useless and teaches people to turn redaction off.
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] ValuePatterns =
    [
        // "Authorization: Bearer eyJ..." and bare JWTs.
        (new Regex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "Bearer " + Placeholder),
        (new Regex(@"\beyJ[A-Za-z0-9\-_]{8,}\.[A-Za-z0-9\-_]{8,}\.[A-Za-z0-9\-_]+", RegexOptions.Compiled),
            Placeholder),

        // KNIGHT's own store credentials, which are prefixed exactly so they can
        // be recognised here and in a secret scanner.
        (new Regex(@"\bknight-[A-Za-z0-9]{6,}-[A-Za-z0-9]{8,}", RegexOptions.Compiled), Placeholder),

        // key=value and "key": "value" pairs whose key looks like a credential.
        (new Regex(
                @"(?<key>password|secret|token|credential|api[_-]?key|client[_-]?secret|passphrase)(?<sep>""?\s*[:=]\s*""?)(?<value>[^\s"",;&}]+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${key}${sep}" + Placeholder),

        // Connection strings, which carry a password in a field nobody named.
        (new Regex(@"(?<key>Password|Pwd)=(?<value>[^;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${key}=" + Placeholder),

        // Credentials embedded in a URL's authority.
        (new Regex(@"(?<scheme>[a-z][a-z0-9+.\-]*://)(?<user>[^:/@\s]+):(?<secret>[^@/\s]+)@", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${scheme}${user}:" + Placeholder + "@"),
    ];

    /// <summary>True when a property with this name must never carry its real value into a sink.</summary>
    public static bool IsSensitiveName(string propertyName) =>
        SensitiveFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Serialises an object with every credential-looking property replaced.
    /// Returns null for null, so a caller can pass an absent before-value through
    /// unchanged.
    /// </summary>
    public static string? Document(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var node = JsonSerializer.SerializeToNode(value, SerializerOptions);

        Scrub(node);

        return node?.ToJsonString();
    }

    /// <summary>
    /// Redacts a JSON document that arrived as text — feature configuration a
    /// store echoed back, an agent's structured job output. Text that is not
    /// valid JSON is redacted as free text instead, never returned untouched.
    /// </summary>
    public static string? Json(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        try
        {
            var node = JsonNode.Parse(json);

            Scrub(node);

            return node?.ToJsonString() ?? Text(json);
        }
        catch (JsonException)
        {
            return Text(json);
        }
    }

    /// <summary>
    /// Removes recognisable secrets from free text: log messages, job output,
    /// exception messages.
    /// </summary>
    public static string? Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var redacted = value;

        foreach (var (pattern, replacement) in ValuePatterns)
        {
            redacted = pattern.Replace(redacted, replacement);
        }

        return redacted;
    }

    private static void Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (IsSensitiveName(property.Key))
                    {
                        obj[property.Key] = Placeholder;

                        continue;
                    }

                    // A value can carry a secret even when its own key does not
                    // name one — "detail": "connecting with password=hunter2".
                    if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[property.Key] = Text(text);

                        continue;
                    }

                    Scrub(property.Value);
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue item && item.TryGetValue<string>(out var text))
                    {
                        array[index] = Text(text);

                        continue;
                    }

                    Scrub(array[index]);
                }

                break;
        }
    }
}
