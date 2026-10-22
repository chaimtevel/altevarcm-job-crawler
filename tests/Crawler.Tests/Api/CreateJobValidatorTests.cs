using Crawler.Api.Jobs;

namespace Crawler.Tests.Api;

public class CreateJobValidatorTests
{
    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("  HTTP://Example.com/path#frag  ", "http://example.com/path")]
    public void Accepts_and_normalizes_http_urls(string url, string expected)
    {
        var (startUrl, errors) = CreateJobValidator.Validate(new CreateJobRequest(url, null));

        Assert.Empty(errors);
        Assert.Equal(expected, startUrl!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com")]
    [InlineData("mailto:a@example.com")]
    public void Rejects_missing_relative_and_non_http_urls(string? url)
    {
        var (startUrl, errors) = CreateJobValidator.Validate(new CreateJobRequest(url, null));

        Assert.Null(startUrl);
        Assert.Contains("url", errors.Keys);
    }

    [Theory]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.168.0.10/admin")]
    [InlineData("http://[::1]/")]
    public void Rejects_local_and_private_addresses(string url)
    {
        var (startUrl, errors) = CreateJobValidator.Validate(new CreateJobRequest(url, null));

        Assert.Null(startUrl);
        Assert.Contains("url", errors.Keys);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void Rejects_max_depth_out_of_range(int maxDepth)
    {
        var (startUrl, errors) = CreateJobValidator.Validate(new CreateJobRequest("https://example.com", maxDepth));

        Assert.Null(startUrl);
        Assert.Contains("maxDepth", errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Accepts_max_depth_within_range(int maxDepth)
    {
        var (_, errors) = CreateJobValidator.Validate(new CreateJobRequest("https://example.com", maxDepth));

        Assert.Empty(errors);
    }
}
