using Crawler.Domain.Crawling;
using Crawler.Domain.Jobs;

namespace Crawler.Api.Jobs;

public static class CreateJobValidator
{
    /// <summary>Validates the request and returns the normalized start URL, or the validation errors.</summary>
    public static (Uri? StartUrl, Dictionary<string, string[]> Errors) Validate(CreateJobRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        Uri? startUrl = null;

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            errors["url"] = ["URL is required."];
        }
        else if (!Uri.TryCreate(WithDefaultScheme(request.Url.Trim()), UriKind.Absolute, out var parsed)
                 || !UrlNormalizer.IsHttp(parsed)
                 || !UrlNormalizer.TryNormalize(parsed.AbsoluteUri, parsed, out startUrl))
        {
            errors["url"] = ["URL must be an absolute http or https address, e.g. https://example.com."];
        }
        else if (NetworkAddress.IsBlockedHost(startUrl.Host))
        {
            startUrl = null;
            errors["url"] = ["URL must point to a public internet address."];
        }

        if (request.MaxDepth is < 0 or > CrawlJob.MaxAllowedDepth)
        {
            errors["maxDepth"] = [$"maxDepth must be between 0 and {CrawlJob.MaxAllowedDepth}."];
        }

        return (errors.Count == 0 ? startUrl : null, errors);
    }

    /// <summary>"example.com/path" becomes https://…; anything with a scheme or a leading slash is left alone.</summary>
    private static string WithDefaultScheme(string url) =>
        url.Contains(':') || url.StartsWith('/') ? url : $"https://{url}";
}
