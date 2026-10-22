using System.Net;
using System.Net.Sockets;

namespace Crawler.Domain.Crawling;

/// <summary>SSRF guard: only public internet addresses may be crawled.</summary>
public static class NetworkAddress
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            _ => false,
        };
    }

    /// <summary>True for "localhost" or a literal non-public IP; DNS names are checked at connect time.</summary>
    public static bool IsBlockedHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && !IsPublic(ip);
    }

    private static bool IsPublicIPv4(byte[] b) => !(
        b[0] == 0                                   // 0.0.0.0/8 "this network"
        || b[0] == 10                               // 10.0.0.0/8 private
        || (b[0] == 100 && b[1] >= 64 && b[1] < 128) // 100.64.0.0/10 carrier-grade NAT
        || b[0] == 127                              // loopback
        || (b[0] == 169 && b[1] == 254)             // link-local, incl. cloud metadata
        || (b[0] == 172 && b[1] >= 16 && b[1] < 32) // 172.16.0.0/12 private
        || (b[0] == 192 && b[1] == 0 && b[2] == 0)  // 192.0.0.0/24 IETF protocol assignments
        || (b[0] == 192 && b[1] == 168)             // 192.168.0.0/16 private
        || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // 198.18.0.0/15 benchmarking
        || b[0] >= 224);                            // multicast, reserved, broadcast

    private static bool IsPublicIPv6(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return !(address.IsIPv6LinkLocal
                 || address.IsIPv6SiteLocal
                 || address.IsIPv6Multicast
                 || (b[0] & 0xFE) == 0xFC                       // fc00::/7 unique local
                 || (b[0] == 0x20 && b[1] == 0x02)              // 2002::/16 6to4, can wrap a private IPv4
                 || b.AsSpan(0, 12).SequenceEqual(Nat64Prefix)  // 64:ff9b::/96 NAT64, wraps an IPv4
                 || b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0); // ::a.b.c.d IPv4-compatible (deprecated)
    }

    private static readonly byte[] Nat64Prefix = [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];
}
