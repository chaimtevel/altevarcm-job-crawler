using System.Net;
using Crawler.Domain.Crawling;

namespace Crawler.Tests.Domain;

public class NetworkAddressTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Private_and_reserved_addresses_are_not_public(string ip)
    {
        Assert.False(NetworkAddress.IsPublic(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void Internet_addresses_are_public(string ip)
    {
        Assert.True(NetworkAddress.IsPublic(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("api.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("example.com", false)]
    [InlineData("8.8.8.8", false)]
    public void Blocks_localhost_and_private_ip_literals(string host, bool blocked)
    {
        Assert.Equal(blocked, NetworkAddress.IsBlockedHost(host));
    }
}
