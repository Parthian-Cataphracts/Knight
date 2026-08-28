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
/// The request is deliberately unsigned, unlike the health probe: this is the
/// bootstrap step, run before the store has been through a handshake and
/// therefore before it holds any key to verify a signature with.
///
/// Both methods are implemented, and they are tried in that order. HTTP first
/// because it is the one an operator can satisfy in a minute with a file, and
/// DNS TXT second because it is the only one available to a store that has no
/// HTTP surface yet — a domain bought this morning, or one pointed at a machine
/// that is still being provisioned. A store proves itself by either.
///
/// The DNS answer is compared and never fetched. Whatever is published at that
/// name is somebody else's string, and the only thing done with it is a
/// fixed-time comparison against a token KNIGHT issued.
/// </summary>
internal sealed class DomainOwnershipVerifier : IDomainOwnershipVerifier
{
    /// <summary>A token is 40-odd characters; anything larger is not a token file.</summary>
    private const int MaxTokenResponseBytes = 4096;

    private readonly IHttpClientFactory _clients;
    private readonly StoreEndpointResolver _endpoints;
    private readonly IDnsTextResolver _dns;
    private readonly ILogger<DomainOwnershipVerifier> _logger;

    public DomainOwnershipVerifier(
        IHttpClientFactory clients,
        StoreEndpointResolver endpoints,
        IDnsTextResolver dns,
        ILogger<DomainOwnershipVerifier> logger)
    {
        _clients = clients;
        _endpoints = endpoints;
        _dns = dns;
        _logger = logger;
    }

    public async Task<DomainVerificationAttempt> VerifyAsync(string domain, string token, CancellationToken cancellationToken)
    {
        var http = await VerifyOverHttpAsync(domain, token, cancellationToken);

        if (http.Verified)
        {
            return http;
        }

        var dns = await VerifyOverDnsAsync(domain, token, cancellationToken);

        // The HTTP refusal is the one reported when neither worked. It is the
        // method most operators are trying, and a message about a DNS record
        // somebody never published would send them to the wrong place.
        return dns.Verified ? dns : http;
    }

    /// <summary>
    /// The TXT record at <c>_knight-verification.&lt;domain&gt;</c>.
    ///
    /// Any of the records at that name may carry the token: publishing a second
    /// TXT record beside an existing one is how this is done on a domain that
    /// already has SPF or a certificate challenge on it, and refusing because
    /// the first record was somebody else's would be refusing the normal case.
    /// </summary>
    private async Task<DomainVerificationAttempt> VerifyOverDnsAsync(
        string domain,
        string token,
        CancellationToken cancellationToken)
    {
        var method = nameof(DomainVerificationMethod.DnsTextRecord);
        var name = DomainVerificationPaths.DnsRecordName(domain);
        var records = await _dns.LookupAsync(name, cancellationToken);

        if (records.Count == 0)
        {
            return new DomainVerificationAttempt(false, method, $"No TXT record was found at {name}.");
        }

        foreach (var record in records)
        {
            if (Matches(record.Trim(), token))
            {
                return new DomainVerificationAttempt(true, method, $"Verified from the TXT record at {name}.");
            }
        }

        return new DomainVerificationAttempt(
            false,
            method,
            $"{name} has {records.Count} TXT record(s) and none of them is the token issued for this store.");
    }

    private async Task<DomainVerificationAttempt> VerifyOverHttpAsync(
        string domain,
        string token,
        CancellationToken cancellationToken)
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
