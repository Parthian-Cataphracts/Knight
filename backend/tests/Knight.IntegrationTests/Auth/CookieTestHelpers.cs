using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Knight.IntegrationTests.Auth;

internal static class CookieTestHelpers
{
    /// <summary>Extracts a cookie's raw value from a response's Set-Cookie headers by name prefix.</summary>
    public static string? ExtractCookieValue(HttpResponseMessage response, string cookieNamePrefix)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return null;
        }

        foreach (var header in setCookieHeaders)
        {
            var match = CookiePattern(cookieNamePrefix).Match(header);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    public static bool TryGetSetCookieHeader(HttpResponseMessage response, string cookieNamePrefix, out string? header)
    {
        header = null;
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return false;
        }

        header = setCookieHeaders.FirstOrDefault(h => h.Contains(cookieNamePrefix, StringComparison.Ordinal));
        return header is not null;
    }

    public static void AttachCookie(HttpRequestMessage request, string cookieName, string cookieValue)
    {
        request.Headers.TryAddWithoutValidation("Cookie", $"{cookieName}={cookieValue}");
    }

    private static Regex CookiePattern(string cookieNamePrefix) =>
        new($@"{Regex.Escape(cookieNamePrefix)}=([^;]+)");
}
