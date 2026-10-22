namespace Crawler.Domain.Crawling;

/// <summary>
/// Converts raw href values into a canonical absolute URL so the same page is recognised
/// no matter how it was linked.
/// </summary>
public static class UrlNormalizer
{
    /// <summary>Longer URLs are rejected; keeps the (job_id, url) unique index under Postgres' btree limit.</summary>
    public const int MaxLength = 2048;

    /// <summary>
    /// Resolves <paramref name="href"/> against <paramref name="pageUrl"/> and normalizes it.
    /// Returns false for empty values, non-http(s) schemes (mailto:, tel:, javascript:, ...) and URLs over <see cref="MaxLength"/>.
    /// </summary>
    /// <remarks>
    /// Normalization: scheme and host lowercased, default port removed, fragment removed,
    /// user info removed. Path case, query string and trailing slashes are preserved because
    /// servers may treat them as distinct resources.
    /// </remarks>
    public static bool TryNormalize(string? href, Uri pageUrl, out Uri normalized)
    {
        normalized = null!;

        if (string.IsNullOrWhiteSpace(href))
            return false;

        if (!Uri.TryCreate(pageUrl, href.Trim(), out var resolved))
            return false;

        if (!IsHttp(resolved))
            return false;

        // HttpRequestUrl = scheme + host + non-default port + path + query (no fragment, no user info).
        var canonical = resolved.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        if (canonical.Length > MaxLength)
            return false;

        normalized = new Uri(canonical);
        return true;
    }

    public static bool IsHttp(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
