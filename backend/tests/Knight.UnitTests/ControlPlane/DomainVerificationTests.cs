using System.Net;
using System.Text;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.ControlPlane.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using Stores;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Proving that whoever controls a domain holds the token KNIGHT issued.
///
/// Modelled since phase 3 and half-built: the HTTP method worked and the DNS one
/// was an enum value with nothing behind it, which left a store that has no HTTP
/// surface yet — a domain bought this morning, or one pointed at a machine still
/// being provisioned — with no way to prove anything.
///
/// Two things are tested here and they are different in kind: the DNS wire
/// format, which is fiddly and deterministic, and the verifier's judgement,
/// which is where a mistake would accept somebody else's domain.
/// </summary>
public sealed class DomainVerificationTests
{
    private const string Token = "knight-verify-0123456789abcdef0123456789abcdef";

    // --- The wire format -------------------------------------------------------

    [Fact]
    public void A_query_asks_for_TXT_at_the_name_it_was_given()
    {
        var query = SystemDnsTextResolver.Encode(0x1234, "_knight-verification.shop.example.com");

        Assert.Equal(0x12, query[0]);
        Assert.Equal(0x34, query[1]);
        Assert.Equal(0x01, query[2]); // recursion desired
        Assert.Equal(1, (query[4] << 8) | query[5]); // one question

        // The labels, length-prefixed, root-terminated, then TXT and IN.
        var name = Encoding.ASCII.GetString(query, 12, query.Length - 12 - 5);
        Assert.Contains("knight-verification", name, StringComparison.Ordinal);
        Assert.Equal(0x10, query[^3]); // TXT
        Assert.Equal(0x01, query[^1]); // IN
    }

    [Fact]
    public void A_label_longer_than_dns_allows_is_refused_rather_than_truncated()
    {
        // Sent as-is it would produce a query no resolver answers, and the
        // failure would look like a domain that has no record.
        Assert.Throws<FormatException>(() => SystemDnsTextResolver.Encode(1, new string('a', 64) + ".example.com"));
    }

    [Fact]
    public void The_txt_strings_are_read_out_of_an_answer()
    {
        var response = Answer(0x1234, [Token, "v=spf1 -all"]);

        Assert.Equal([Token, "v=spf1 -all"], SystemDnsTextResolver.Decode(response, 0x1234));
    }

    [Fact]
    public void A_long_record_arrives_as_several_strings_and_is_joined()
    {
        var half = new string('a', 200);
        var response = Answer(0x1234, [half + half], splitAt: 200);

        // A TXT value over 255 characters is published as several strings.
        // Every resolver joins them and anybody publishing one expects that.
        Assert.Equal(half + half, Assert.Single(SystemDnsTextResolver.Decode(response, 0x1234)));
    }

    [Fact]
    public void An_answer_to_somebody_elses_question_is_ignored()
    {
        var response = Answer(0x4321, [Token]);

        // A stray datagram on the socket is not an answer to this query.
        Assert.Empty(SystemDnsTextResolver.Decode(response, 0x1234));
    }

    [Fact]
    public void A_truncated_answer_is_not_read()
    {
        var response = Answer(0x1234, [Token], truncated: true);

        // A token is forty-odd characters, so truncation means somebody has put
        // something else at that name. Reporting nothing is honest; a TCP retry
        // would be fetching it.
        Assert.Empty(SystemDnsTextResolver.Decode(response, 0x1234));
    }

    [Fact]
    public void An_error_response_is_not_read()
    {
        var response = Answer(0x1234, [Token], rcode: 3); // NXDOMAIN

        Assert.Empty(SystemDnsTextResolver.Decode(response, 0x1234));
    }

    [Fact]
    public void A_compression_pointer_that_points_at_itself_does_not_hang()
    {
        var response = Answer(0x1234, [Token], selfReferentialPointer: true);

        // The one place a DNS parser is traditionally exploited. A pointer ends
        // the name here, so there is nothing to loop over.
        _ = SystemDnsTextResolver.Decode(response, 0x1234);
    }

    // --- The judgement ---------------------------------------------------------

    private sealed class Records(params string[] values) : IDnsTextResolver
    {
        public string? Asked { get; private set; }

        public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken)
        {
            Asked = name;
            return Task.FromResult<IReadOnlyList<string>>(values);
        }
    }

    private static DomainOwnershipVerifier Verifier(IDnsTextResolver dns) =>
        new(
            new UnreachableClients(),
            new StoreEndpointResolver(Microsoft.Extensions.Options.Options.Create(new StoreProbeOptions())),
            dns,
            NullLogger<DomainOwnershipVerifier>.Instance);

    /// <summary>A store with no HTTP surface at all, which is the case DNS exists for.</summary>
    private sealed class UnreachableClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refusing());

        private sealed class Refusing : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("nothing is listening");
        }
    }

    [Fact]
    public async Task A_token_published_as_a_TXT_record_verifies_a_domain_with_no_http_surface()
    {
        var dns = new Records(Token);

        var attempt = await Verifier(dns).VerifyAsync("shop.example.com", Token, CancellationToken.None);

        Assert.True(attempt.Verified);
        Assert.Equal("DnsTextRecord", attempt.Method);
        Assert.Equal("_knight-verification.shop.example.com", dns.Asked);
    }

    [Fact]
    public async Task A_token_beside_somebody_elses_records_still_verifies()
    {
        // Publishing a second TXT record beside SPF or a certificate challenge
        // is how this is done on a domain that is already in use.
        var attempt = await Verifier(new Records("v=spf1 -all", Token))
            .VerifyAsync("shop.example.com", Token, CancellationToken.None);

        Assert.True(attempt.Verified);
    }

    [Fact]
    public async Task Another_stores_token_does_not_verify_this_one()
    {
        var attempt = await Verifier(new Records("knight-verify-somebody-elses-token"))
            .VerifyAsync("shop.example.com", Token, CancellationToken.None);

        Assert.False(attempt.Verified);
    }

    [Fact]
    public async Task A_record_that_merely_contains_the_token_does_not_verify()
    {
        var attempt = await Verifier(new Records($"the token is {Token} apparently"))
            .VerifyAsync("shop.example.com", Token, CancellationToken.None);

        // Exact after trimming. A page — or a record — that echoes the token
        // back is not proof that anybody chose to publish it.
        Assert.False(attempt.Verified);
    }

    [Fact]
    public async Task When_neither_method_worked_the_http_refusal_is_the_one_reported()
    {
        var attempt = await Verifier(new Records()).VerifyAsync("shop.example.com", Token, CancellationToken.None);

        Assert.False(attempt.Verified);

        // HTTP is the method most operators are trying. A message about a DNS
        // record they never published would send them to the wrong place.
        Assert.Equal("HttpToken", attempt.Method);
    }

    // --- Building a response ---------------------------------------------------

    private static byte[] Answer(
        ushort id,
        string[] records,
        bool truncated = false,
        int rcode = 0,
        int? splitAt = null,
        bool selfReferentialPointer = false)
    {
        var message = new List<byte>
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            (byte)(0x81 | (truncated ? 0x02 : 0x00)), (byte)(0x80 | rcode),
            0x00, 0x00,                                    // no question echoed back
            0x00, (byte)records.Length,                    // answers
            0x00, 0x00,
            0x00, 0x00,
        };

        foreach (var record in records)
        {
            if (selfReferentialPointer)
            {
                message.AddRange([0xC0, (byte)message.Count]);
            }
            else
            {
                message.AddRange([0xC0, 0x0C]); // a compressed name
            }

            message.AddRange([0x00, 0x10]); // TXT
            message.AddRange([0x00, 0x01]); // IN
            message.AddRange([0x00, 0x00, 0x00, 0x3C]); // ttl

            var strings = new List<byte>();

            foreach (var chunk in Split(record, splitAt ?? 255))
            {
                strings.Add((byte)chunk.Length);
                strings.AddRange(Encoding.ASCII.GetBytes(chunk));
            }

            message.AddRange([(byte)(strings.Count >> 8), (byte)(strings.Count & 0xFF)]);
            message.AddRange(strings);
        }

        return [.. message];
    }

    private static IEnumerable<string> Split(string value, int size)
    {
        for (var offset = 0; offset < value.Length; offset += size)
        {
            yield return value.Substring(offset, Math.Min(size, value.Length - offset));
        }
    }
}
