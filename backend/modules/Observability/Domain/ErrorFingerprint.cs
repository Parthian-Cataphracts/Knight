using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Observability.Domain;

/// <summary>
/// Turns one reported error into the stable identity of the *problem* behind it
/// ([`adr/0013`](../../../docs/adr/0013-error-grouping-strategy.md)).
///
/// The whole value of this class is what it deliberately throws away. Line
/// numbers, concrete ids, memory addresses and the store's own version are all
/// excluded, because none of them distinguishes one problem from another — they
/// only fragment a single problem into a hundred groups the moment somebody
/// deploys. What is kept is the smallest set of signals two operators would agree
/// means "the same thing is broken": which store, which environment, which
/// exception, where in the application code, and which route.
///
/// It is imperfect in both directions and known to be: it over-groups when one
/// helper fails for unrelated reasons, and under-groups when a framework moves a
/// frame. That is why <see cref="Version"/> exists. Changing the algorithm
/// changes the fingerprints it produces, and old groups must not silently start
/// collecting new events under an identity computed by different rules — so the
/// version is stored on every group and bumped whenever anything below changes.
/// </summary>
public static class ErrorFingerprint
{
    /// <summary>
    /// The algorithm version recorded on every group. Bump whenever the
    /// normalisation below changes in a way that alters its output.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// How many in-application frames take part. Enough to tell two call paths
    /// through the same helper apart, few enough that a refactor further up the
    /// stack does not split the group.
    /// </summary>
    private const int StackFrameCount = 5;

    /// <summary>
    /// Frames belonging to the runtime, the framework or an installed package.
    /// A traceback through Django's ORM says nothing about which problem this is;
    /// the first frame in the store's own code says nearly everything.
    /// </summary>
    private static readonly string[] VendorPathMarkers =
    [
        "/site-packages/",
        "/dist-packages/",
        "/lib/python",
        "/django/",
        "/rest_framework/",
        "/gunicorn/",
        "/celery/",
    ];

    private static readonly Regex FramePattern = new(
        @"^\s*File\s+""(?<file>[^""]+)"",\s+line\s+(?<line>\d+),\s+in\s+(?<func>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Uuid = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static readonly Regex HexAddress = new("0x[0-9a-fA-F]+", RegexOptions.Compiled);

    private static readonly Regex Number = new(@"\d+", RegexOptions.Compiled);

    private static readonly Regex QuotedLiteral = new(@"'[^']*'|""[^""]*""", RegexOptions.Compiled);

    /// <summary>
    /// Computes the fingerprint and the human-readable title that goes with it.
    ///
    /// The title is derived here rather than by the caller so that every group
    /// created from the same problem is labelled identically, whichever event
    /// happened to arrive first.
    /// </summary>
    public static ErrorFingerprintResult Compute(
        Guid storeId,
        string environment,
        string exceptionType,
        string? message,
        string? endpoint,
        string? stackTrace)
    {
        var type = (exceptionType ?? string.Empty).Trim();
        var stackTop = NormaliseStackTop(stackTrace);
        var route = NormaliseEndpoint(endpoint);

        // The pipe cannot occur in a normalised component, so no two different
        // inputs can be joined into the same string.
        var material = string.Join(
            '|',
            storeId.ToString("N"),
            (environment ?? string.Empty).Trim().ToLowerInvariant(),
            type,
            stackTop,
            route);

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));

        return new ErrorFingerprintResult(hash, Version, Title(type, message), route, stackTop);
    }

    /// <summary>
    /// The route template, never the concrete URL. <c>/api/orders/5182/</c> and
    /// <c>/api/orders/5183/</c> are the same endpoint failing twice, and an
    /// operator who sees them as two problems will fix neither.
    /// </summary>
    public static string NormaliseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return string.Empty;
        }

        var path = endpoint.Trim();

        // Query strings carry request data, never identity, and are exactly the
        // sort of thing an upstream scrubber may or may not have cleaned.
        var queryStart = path.IndexOf('?', StringComparison.Ordinal);

        if (queryStart >= 0)
        {
            path = path[..queryStart];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(PlaceholderIfLiteral)
            .ToArray();

        return "/" + string.Join('/', segments);
    }

    private static string PlaceholderIfLiteral(string segment)
    {
        if (Uuid.IsMatch(segment))
        {
            return "{id}";
        }

        // A segment that is entirely digits is an identifier. A segment that
        // merely contains one — "v1", "orders2" — is a name, and replacing it
        // would merge genuinely different routes.
        return segment.All(char.IsAsciiDigit) ? "{id}" : segment.ToLowerInvariant();
    }

    /// <summary>
    /// The top in-application frames, stripped of everything that moves between
    /// deployments.
    ///
    /// Falls back to the empty string rather than to the raw trace when no
    /// application frame can be found: a fingerprint over an un-normalised trace
    /// would look precise and behave like no grouping at all.
    /// </summary>
    public static string NormaliseStackTop(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return string.Empty;
        }

        var frames = new List<string>(StackFrameCount);

        foreach (var line in stackTrace.Split('\n'))
        {
            var match = FramePattern.Match(line);

            if (!match.Success)
            {
                continue;
            }

            var file = match.Groups["file"].Value.Replace('\\', '/');

            if (VendorPathMarkers.Any(marker => file.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Absolute deployment paths differ per machine and per release; the
            // tail is what identifies the file.
            frames.Add($"{Relativise(file)}:{match.Groups["func"].Value}");

            if (frames.Count == StackFrameCount)
            {
                break;
            }
        }

        return string.Join(';', frames);
    }

    /// <summary>
    /// Keeps the last few path segments. A store deployed to
    /// <c>/srv/app-2026-08-19/apps/orders/views.py</c> and the same store
    /// deployed an hour later must produce the same frame.
    /// </summary>
    private static string Relativise(string file)
    {
        var segments = file.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length <= 3 ? string.Join('/', segments) : string.Join('/', segments[^3..]);
    }

    /// <summary>
    /// A one-line label for the group: the exception type and a message with its
    /// variable parts removed, so a hundred "duplicate key ... (id)=(5182)"
    /// messages read as one title rather than a hundred near-identical ones.
    /// </summary>
    public static string Title(string exceptionType, string? message)
    {
        var normalised = NormaliseMessage(message);

        var title = string.IsNullOrEmpty(normalised)
            ? exceptionType
            : $"{exceptionType}: {normalised}";

        return title.Length <= ErrorGroup.MaxTitleLength ? title : title[..ErrorGroup.MaxTitleLength];
    }

    public static string NormaliseMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var single = message.Trim().Split('\n')[0].Trim();

        single = Uuid.Replace(single, "{uuid}");
        single = HexAddress.Replace(single, "{addr}");
        single = QuotedLiteral.Replace(single, "{value}");
        single = Number.Replace(single, "{n}");

        return single.Length <= 300 ? single : single[..300];
    }
}

/// <summary>
/// What <see cref="ErrorFingerprint.Compute"/> produced: the identity, the
/// algorithm version behind it, and the derived fields a new group is created
/// from.
/// </summary>
public sealed record ErrorFingerprintResult(
    string Fingerprint,
    int FingerprintVersion,
    string Title,
    string EndpointTemplate,
    string NormalisedStackTop);
