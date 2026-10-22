using System.Text;
using Crawler.Domain.Crawling;
using Polly.Timeout;

namespace Crawler.Worker.Crawling;

public enum FetchStatus { Html, NotHtml, Failed }

public record FetchResult(FetchStatus Status, int? HttpStatusCode, string? Html, Uri? FinalUrl, string? Reason);

/// <summary>Downloads a page. Network and HTTP errors become a Failed result; they never throw.</summary>
public class PageFetcher(HttpClient http)
{
    private const int MaxContentBytes = 5 * 1024 * 1024;

    /// <summary>Auto-redirect is off, so this one limit covers every hop, including https→http.</summary>
    private const int MaxRedirects = 5;

    /// <summary>Covers headers and body; the resilience pipeline alone only covers the headers.</summary>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);

    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TotalTimeout);

        try
        {
            var (response, failure) = await GetFollowingRedirectsAsync(url, timeout.Token);
            if (response is null)
            {
                return failure!;
            }

            using (response)
            {
                return await ReadResponseAsync(response, url, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new(FetchStatus.Failed, null, null, null, $"Request failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new(FetchStatus.Failed, null, null, null, "Timed out");
        }
    }

    /// <summary>Downloads a text file such as robots.txt or a sitemap; null on any failure.</summary>
    public async Task<string?> FetchTextAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TotalTimeout);

        try
        {
            var (response, _) = await GetFollowingRedirectsAsync(url, timeout.Token);
            if (response is null)
            {
                return null;
            }

            using (response)
            {
                return response.IsSuccessStatusCode ? await ReadLimitedAsync(response.Content, timeout.Token) : null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return null;
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Follows redirects up to the limit; returns the final response (caller disposes) or a failure.</summary>
    private async Task<(HttpResponseMessage? Response, FetchResult? Failure)> GetFollowingRedirectsAsync(
        Uri url, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;

            if (status is < 300 or >= 400 || response.Headers.Location is not { } location)
            {
                return (response, null);
            }

            var requested = response.RequestMessage?.RequestUri ?? current;
            response.Dispose();
            if (hop >= MaxRedirects)
            {
                return (null, new(FetchStatus.Failed, status, null, null, "Too many redirects"));
            }

            if (!Uri.TryCreate(requested, location, out var next)
                || !UrlNormalizer.IsHttp(next))
            {
                return (null, new(FetchStatus.Failed, status, null, null, $"Redirects to unsupported URL {location}"));
            }

            current = next;
        }
    }

    private static async Task<FetchResult> ReadResponseAsync(HttpResponseMessage response, Uri requested, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return new(FetchStatus.Failed, status, null, null, $"HTTP {status}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!IsHtml(mediaType))
        {
            return new(FetchStatus.NotHtml, status, null, null, $"Not HTML ({mediaType ?? "unknown type"})");
        }

        var html = await ReadLimitedAsync(response.Content, ct);
        if (html is null)
        {
            return new(FetchStatus.Failed, status, null, null, "Page larger than 5 MB");
        }

        // After redirects, relative links resolve against the final URL.
        return new(FetchStatus.Html, status, html, response.RequestMessage?.RequestUri ?? requested, null);
    }

    private static bool IsHtml(string? mediaType) =>
        string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxContentBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return GetEncoding(content.Headers.ContentType?.CharSet).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static Encoding GetEncoding(string? charset)
    {
        try
        {
            return string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
