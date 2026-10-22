using System.Net;
using System.Net.Sockets;
using Crawler.Domain.Crawling;

namespace Crawler.Worker.Crawling;

/// <summary>SSRF guard at connect time: covers DNS names and redirects, not just the submitted URL.</summary>
public static class PublicNetworkConnector
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(NetworkAddress.IsPublic).ToArray();

        if (allowed.Length == 0)
            throw new HttpRequestException($"Blocked: {host} resolves only to private or reserved addresses.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
