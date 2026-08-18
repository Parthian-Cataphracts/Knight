namespace Knight.Api.Composition;

/// <summary>
/// Refresh-token cookie transport. Platform and Tenant sessions use distinct
/// names and paths so they can never collide, are read only by their own
/// auth route group, and are otherwise invisible to other tenant domains.
/// See docs/architecture/authorization.md ("refresh cookie strategy").
///
/// Outside Development the cookie name carries the browser-enforced "__Host-"
/// prefix, which requires Secure=true, no Domain attribute, and Path=/ — so it
/// is only used when we can actually guarantee HTTPS. Development (where HTTPS
/// is commonly unavailable locally) uses a plain name and relaxes Secure, but
/// this must never happen outside Development — Production always gets the
/// full "__Host-" treatment.
/// </summary>
public static class AuthCookies
{
    private const string PlatformCookieBaseName = "platform-rt";
    private const string TenantCookieBaseName = "tenant-rt";

    private const string PlatformPath = "/api/platform/auth";
    private const string TenantPath = "/api/tenant/auth";

    public static void AppendPlatformRefreshCookie(this HttpResponse response, string rawToken, DateTimeOffset expiresAt, IWebHostEnvironment environment) =>
        Append(response, PlatformCookieBaseName, PlatformPath, rawToken, expiresAt, environment);

    public static void AppendTenantRefreshCookie(this HttpResponse response, string rawToken, DateTimeOffset expiresAt, IWebHostEnvironment environment) =>
        Append(response, TenantCookieBaseName, TenantPath, rawToken, expiresAt, environment);

    public static void DeletePlatformRefreshCookie(this HttpResponse response, IWebHostEnvironment environment) =>
        Delete(response, PlatformCookieBaseName, PlatformPath, environment);

    public static void DeleteTenantRefreshCookie(this HttpResponse response, IWebHostEnvironment environment) =>
        Delete(response, TenantCookieBaseName, TenantPath, environment);

    public static string? ReadPlatformRefreshCookie(this HttpRequest request, IWebHostEnvironment environment) =>
        request.Cookies[CookieName(PlatformCookieBaseName, environment)];

    public static string? ReadTenantRefreshCookie(this HttpRequest request, IWebHostEnvironment environment) =>
        request.Cookies[CookieName(TenantCookieBaseName, environment)];

    private static void Append(HttpResponse response, string baseName, string path, string rawToken, DateTimeOffset expiresAt, IWebHostEnvironment environment)
    {
        response.Cookies.Append(CookieName(baseName, environment), rawToken, BuildOptions(path, environment, expiresAt));
    }

    private static void Delete(HttpResponse response, string baseName, string path, IWebHostEnvironment environment)
    {
        response.Cookies.Delete(CookieName(baseName, environment), BuildOptions(path, environment, expires: null));
    }

    private static string CookieName(string baseName, IWebHostEnvironment environment) =>
        environment.IsDevelopment() ? baseName : $"__Host-{baseName}";

    private static CookieOptions BuildOptions(string path, IWebHostEnvironment environment, DateTimeOffset? expires)
    {
        var isDevelopment = environment.IsDevelopment();

        return new CookieOptions
        {
            HttpOnly = true,
            Secure = !isDevelopment,
            SameSite = isDevelopment ? SameSiteMode.Lax : SameSiteMode.Strict,
            Path = path,
            IsEssential = true,
            Expires = expires
        };
    }
}
