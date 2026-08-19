using System.Globalization;
using System.Text;
using Knight.Contracts.Ingest;

namespace Knight.Api.Ingest;

/// <summary>
/// The canonical form the entitlement payload is signed over.
///
/// It is a flat string rather than "the JSON body", because two sides in two
/// languages will never agree on JSON byte-for-byte — property order, whitespace
/// and date formatting all differ — and a signature that only sometimes verifies
/// is worse than none. Timestamps are Unix seconds for the same reason:
/// integers mean the same thing in C# and Python, and ISO-8601 fractional
/// seconds do not.
///
/// The version prefix is what lets this change later: a store that knows only
/// version 1 refuses a version 2 payload rather than mis-verifying it. The exact
/// form is part of the store contract — see
/// <c>docs/contracts/store-integration.schema.json</c>, which both sides test
/// against.
/// </summary>
public static class EntitlementSignature
{
    public const string Version = "1";

    public static string Canonicalise(EntitlementSetResponse payload)
    {
        var builder = new StringBuilder();

        builder.Append("knight-entitlements|")
            .Append(Version).Append('|')
            .Append(payload.StoreId.ToString("D")).Append('|')
            .Append(payload.CustomerId.ToString("D")).Append('|')
            .Append(payload.Environment).Append('|')
            .Append(Seconds(payload.IssuedAt)).Append('|')
            .Append(Seconds(payload.StaleAfter)).Append('|');

        // Ordinal sort, so the order of the signed set never depends on the
        // database's collation or on the culture the process happens to run in.
        var features = payload.Features
            .OrderBy(feature => feature.Slug, StringComparer.Ordinal)
            .Select(feature => $"{feature.Slug}:{(feature.ExpiresAt is { } expires ? Seconds(expires) : "-")}");

        builder.Append(string.Join(",", features));

        return builder.ToString();
    }

    private static string Seconds(DateTimeOffset moment) =>
        moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
