using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>Reads the TXT records published at a name.</summary>
/// <remarks>
/// A port rather than a static call, so the verifier above it can be tested
/// without a network and without a domain somebody has to keep publishing a
/// record on.
/// </remarks>
public interface IDnsTextResolver
{
    /// <summary>
    /// Every TXT string at <paramref name="name"/>, or an empty list.
    ///
    /// Never throws: a name that does not exist, a resolver that did not answer
    /// and a record that is not there are the same answer to the only question
    /// being asked — is the token published — and distinguishing them would
    /// make every caller handle three cases that have one response.
    /// </summary>
    Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// A TXT lookup against this machine's own resolvers, in about a hundred lines
/// of DNS.
///
/// Written rather than taken from a package, for the reason the agent gives for
/// having no dependencies at all: this is one query type, one record type and a
/// parser that has to be careful about exactly one thing (compression
/// pointers). A dependency for that is a dependency to keep patched on a host
/// nobody wants to think about.
///
/// **UDP only, and it says so when an answer is truncated.** A verification
/// token is forty-odd characters, so the answer fits in a single datagram many
/// times over; a truncated response means somebody has put something else at
/// that name, and reporting it honestly is more useful than a TCP retry that
/// would fetch it.
///
/// The resolvers are the machine's own, never a name from the domain being
/// checked. The answer is compared and never fetched, so a hostile record is a
/// string that fails a comparison rather than a request KNIGHT makes on
/// somebody's behalf.
/// </summary>
internal sealed class SystemDnsTextResolver : IDnsTextResolver
{
    /// <summary>Long enough for any legitimate answer here; short enough that a silent resolver is not a stall.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly ILogger<SystemDnsTextResolver> _logger;

    public SystemDnsTextResolver(ILogger<SystemDnsTextResolver> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken)
    {
        foreach (var server in Resolvers())
        {
            try
            {
                var answer = await QueryAsync(server, name, cancellationToken);

                if (answer.Count > 0)
                {
                    return answer;
                }
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException or FormatException)
            {
                // The next resolver, or none. A machine whose first resolver is
                // unreachable is an ordinary machine, and a verification that
                // failed for that reason would send an operator looking at their
                // DNS records rather than at their network.
                _logger.LogDebug(exception, "TXT lookup for {Name} via {Server} did not answer.", name, server);
            }
        }

        return [];
    }

    /// <summary>This machine's resolvers, in the order the system lists them.</summary>
    private static IEnumerable<IPAddress> Resolvers()
    {
        var seen = new HashSet<IPAddress>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus is not OperationalStatus.Up)
            {
                continue;
            }

            foreach (var address in adapter.GetIPProperties().DnsAddresses)
            {
                // Link-local IPv6 resolvers need a scope id to be usable and are
                // routinely listed on adapters that cannot reach them.
                if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
                {
                    continue;
                }

                if (seen.Add(address))
                {
                    yield return address;
                }
            }
        }
    }

    private static async Task<IReadOnlyList<string>> QueryAsync(
        IPAddress server,
        string name,
        CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(server.AddressFamily);
        socket.Connect(server, 53);

        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = Encode(id, name);

        await socket.SendAsync(query, cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        var response = await socket.ReceiveAsync(deadline.Token);

        return Decode(response.Buffer, id);
    }

    /// <summary>One standard recursive query for TXT at <paramref name="name"/>.</summary>
    internal static byte[] Encode(ushort id, string name)
    {
        var body = new List<byte>(64)
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            0x01, 0x00,   // recursion desired
            0x00, 0x01,   // one question
            0x00, 0x00,   // no answers
            0x00, 0x00,   // no authority records
            0x00, 0x00,   // no additional records
        };

        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);

            if (bytes.Length > 63)
            {
                throw new FormatException($"'{label}' is longer than a DNS label may be.");
            }

            body.Add((byte)bytes.Length);
            body.AddRange(bytes);
        }

        body.Add(0x00);           // root label
        body.AddRange([0x00, 0x10]); // TXT
        body.AddRange([0x00, 0x01]); // IN

        return [.. body];
    }

    /// <summary>
    /// The TXT strings out of a response, or nothing.
    ///
    /// Nothing is returned for every kind of "no": a mismatched id (a stray
    /// datagram), a truncated answer, an error rcode, a name that does not
    /// exist. Each of those means the token is not published at that name, which
    /// is the whole question.
    /// </summary>
    internal static IReadOnlyList<string> Decode(byte[] response, ushort expectedId)
    {
        if (response.Length < 12)
        {
            return [];
        }

        var id = (ushort)((response[0] << 8) | response[1]);
        var flags = (response[2] << 8) | response[3];
        var truncated = (flags & 0x0200) != 0;
        var rcode = flags & 0x000F;

        if (id != expectedId || truncated || rcode != 0)
        {
            return [];
        }

        var questions = (response[4] << 8) | response[5];
        var answers = (response[6] << 8) | response[7];
        var offset = 12;

        for (var question = 0; question < questions; question++)
        {
            offset = SkipName(response, offset);
            offset += 4; // type and class
        }

        var records = new List<string>();

        for (var answer = 0; answer < answers && offset + 10 <= response.Length; answer++)
        {
            offset = SkipName(response, offset);

            if (offset + 10 > response.Length)
            {
                break;
            }

            var type = (response[offset] << 8) | response[offset + 1];
            var length = (response[offset + 8] << 8) | response[offset + 9];
            offset += 10;

            if (offset + length > response.Length)
            {
                break;
            }

            if (type == 16)
            {
                // A TXT record is one or more length-prefixed strings, and a
                // value longer than 255 characters arrives as several. They are
                // concatenated, which is what every resolver does and what
                // anybody publishing a long token expects.
                var end = offset + length;
                var value = new StringBuilder();

                for (var cursor = offset; cursor < end && cursor < response.Length;)
                {
                    var size = response[cursor];
                    cursor++;

                    if (cursor + size > end)
                    {
                        break;
                    }

                    value.Append(Encoding.ASCII.GetString(response, cursor, size));
                    cursor += size;
                }

                records.Add(value.ToString());
            }

            offset += length;
        }

        return records;
    }

    /// <summary>
    /// Steps past a name, following a compression pointer without following it
    /// anywhere.
    ///
    /// The one place a DNS parser is traditionally exploited: a pointer that
    /// points at itself is an infinite loop inside a control plane. A pointer
    /// ends the name, so there is nothing to loop over.
    /// </summary>
    private static int SkipName(byte[] response, int offset)
    {
        while (offset < response.Length)
        {
            var length = response[offset];

            if (length == 0)
            {
                return offset + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                return offset + 2;
            }

            offset += length + 1;
        }

        return offset;
    }
}
