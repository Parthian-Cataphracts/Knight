using System.Net.Http.Headers;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// Shared HTTP client construction for the catalog suites, matching the pattern
/// used by the AccessControl tests: the tenant is resolved from the request host
/// by the tenant-resolution middleware, and the caller's identity/permissions
/// come from the bearer token.
/// </summary>
internal static class CatalogTestClient
{
    internal static HttpClient For(PostgresApiFixture fixture, string host, string? bearerToken = null)
    {
        var client = fixture.Factory.CreateClient();
        client.BaseAddress = new Uri($"http://{host}");

        if (bearerToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return client;
    }

    internal static HttpClient For(PostgresApiFixture fixture, CatalogTenantContext tenant) =>
        For(fixture, tenant.Host, tenant.Token);

    internal static HttpClient For(PostgresApiFixture fixture, OrderingTenantContext tenant) =>
        For(fixture, tenant.Host, tenant.Token);

    internal static HttpClient For(PostgresApiFixture fixture, CustomerTenantContext tenant) =>
        For(fixture, tenant.Host, tenant.Token);
}
