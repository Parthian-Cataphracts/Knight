using System.Net;
using System.Net.Sockets;
using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.Options;

namespace Knight.Infrastructure.ControlPlane.Integration;

/// <summary>
/// The egress rule for every outbound call KNIGHT makes to a store.
///
/// It refuses on the resolved <em>address</em>, never on the hostname. A
/// hostname check is worthless here: "shop.example.com" resolving to
/// 127.0.0.1 or 169.254.169.254 is the ordinary shape of an SSRF attempt, and
/// the attacker owns the DNS record. The address is checked immediately before
/// the socket connects, so a name that resolves differently a second later
/// cannot slip past a check made earlier
/// (docs/security-threat-model.md, SSRF).
/// </summary>
internal sealed class OutboundAddressPolicy : IOutboundAddressPolicy
{
    private readonly StoreProbeOptions _options;

    public OutboundAddressPolicy(IOptions<StoreProbeOptions> options)
    {
        _options = options.Value;
    }

    public string? Refuse(IPAddress address)
    {
        // An IPv4 address wearing an IPv6 costume is still that IPv4 address;
        // unwrapping first is what stops ::ffff:127.0.0.1 from reading as an
        // ordinary global IPv6 address.
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(candidate))
        {
            return _options.AllowPrivateNetworks ? null : "loopback addresses are not reachable from the control plane";
        }

        if (candidate.Equals(IPAddress.Any) || candidate.Equals(IPAddress.IPv6Any) || candidate.Equals(IPAddress.None))
        {
            return "unspecified addresses are not routable";
        }

        if (IsMulticast(candidate))
        {
            return "multicast addresses are not a store";
        }

        if (IsLinkLocal(candidate))
        {
            // Never allowed, whatever the configuration says: this is the range
            // cloud metadata services live on, and nothing legitimate about a
            // store is reachable there.
            return "link-local addresses are refused";
        }

        if (IsPrivate(candidate))
        {
            return _options.AllowPrivateNetworks ? null : "private addresses are not reachable from the control plane";
        }

        return null;
    }

    private static bool IsMulticast(IPAddress address) =>
        address.AddressFamily is AddressFamily.InterNetwork
            ? address.GetAddressBytes()[0] is >= 224 and <= 239
            : address.IsIPv6Multicast;

    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.AddressFamily is AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal;
        }

        var octets = address.GetAddressBytes();
        return octets[0] is 169 && octets[1] is 254;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily is AddressFamily.InterNetworkV6)
        {
            // fc00::/7 — unique local, the IPv6 equivalent of RFC 1918.
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC || address.IsIPv6SiteLocal;
        }

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,
            127 => true,
            172 => octets[1] is >= 16 and <= 31,
            192 => octets[1] is 168,

            // 100.64.0.0/10, carrier-grade NAT: not the public internet, and not
            // somewhere a managed store is published.
            100 => octets[1] is >= 64 and <= 127,
            _ => false,
        };
    }
}
