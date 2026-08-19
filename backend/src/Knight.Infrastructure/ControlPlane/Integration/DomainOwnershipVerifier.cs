using System.Security.Cryptography;
using System.Text;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Logging;
using Stores;
using Stores.Domain;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// Proves that whoever controls a store's primary domain also holds the token
/// KNIGHT issued, by fetching it from a fixed path on that domain.
///
/// The check runs through the same egress policy as the health poller, so a
/// domain resolving to an internal address fails verification instead of making
/// KNIGHT fetch something on an attacker's behalf. The comparison is fixed-time
/// and exact after trimming: a page that merely <em>contains</em> the token —
/// an error page echoing the request, say — is not proof of anything
/// (docs/security-threat-model.md).
///
/// Only the HTTP method is implemented. The DNS TXT alternative is recorded in
/// the model because it is how a store with no HTTP surface yet will prove
/// itself during provisioning (phase 9); until that exists, offering it here
/// would be a switch that cannot be turned on.
/// </summary>
internal sealed class DomainOwnershipVerifier : IDomainOwnershipVerifier
{
    /// <summary>A token is 40-odd characters; anything larger is not a token file.</summary>
    private const int MaxTokenResponseBytes = 4096;

    private readonly IHttpClientFactory _clients;
    private readonly StoreEndpointResolver _endpoints;
    private readonly ILogger<DomainOwnershipVerifier> _logger;

    public DomainOwnershipVerifier(
        IHttpClientFactory clients,
        StoreEndpointResolver endpoints,
        ILogger<DomainOwnershipVerifier> logger)
    {
        _clients = clients;
        _endpoints = endpoints;
        _logger = logger;
    }

    public async Task<DomainVerificationAttempt> VerifyAsync(string domain, string token, CancellationToken cancellationToken)
    {
        var method = nameof(DomainVerificationMethod.HttpToken);
        var url = _endpoints.Resolve(domain, DomainVerificationPaths.HttpPath);
        var client = _clients.CreateClient(StoreOutboundHttp.ClientName);

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new DomainVerificationAttempt(
                    false,
                    method,
                    $"{DomainVerificationPaths.HttpPath} answered HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxTokenResponseBytes];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
            var published = Encoding.UTF8.GetString(buffer, 0, read).Trim();

            if (!Matches(published, token))
            {
                return new DomainVerificationAttempt(false, method, "The published token does not match the one issued for this store.");
            }

            return new DomainVerificationAttempt(true, method, $"Verified at {url}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogInformation(
                "Domain verification for {Domain} could not reach {Path}",
                domain,
                DomainVerificationPaths.HttpPath);

            return new DomainVerificationAttempt(
                false,
                method,
                $"{DomainVerificationPaths.HttpPath} could not be reached on this domain.");
        }
    }

    private static bool Matches(string published, string expected) =>
        published.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(published),
            Encoding.UTF8.GetBytes(expected));
}
