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
        || (b[0] == 192 && b[1] == 168)             // 192.168.0.0/16 private
        || b[0] >= 224);                            // multicast, reserved, broadcast

    private static bool IsPublicIPv6(IPAddress address)
    {
        var first = address.GetAddressBytes()[0];
        return !(address.IsIPv6LinkLocal
                 || address.IsIPv6SiteLocal
                 || address.IsIPv6Multicast
                 || (first & 0xFE) == 0xFC); // fc00::/7 unique local
    }
}
