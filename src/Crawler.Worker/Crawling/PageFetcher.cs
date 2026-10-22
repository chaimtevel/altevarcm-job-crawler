using System.Text;
using Polly.Timeout;

namespace Crawler.Worker.Crawling;

public enum FetchStatus { Html, NotHtml, Failed }

public record FetchResult(FetchStatus Status, int? HttpStatusCode, string? Html, Uri? FinalUrl, string? Reason);

/// <summary>Downloads a page. Network and HTTP errors become a Failed result; they never throw.</summary>
public class PageFetcher(HttpClient http)
{
    private const int MaxContentBytes = 5 * 1024 * 1024;

    /// <summary>Covers headers and body; the resilience pipeline alone only covers the headers.</summary>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);

    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TotalTimeout);

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
                return new(FetchStatus.Failed, status, null, null, $"HTTP {status}");

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!IsHtml(mediaType))
                return new(FetchStatus.NotHtml, status, null, null, $"Not HTML ({mediaType ?? "unknown type"})");

            var html = await ReadLimitedAsync(response.Content, timeout.Token);
            if (html is null)
                return new(FetchStatus.Failed, status, null, null, "Page larger than 5 MB");

            // After redirects, relative links resolve against the final URL.
            return new(FetchStatus.Html, status, html, response.RequestMessage?.RequestUri ?? url, null);
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
                return null;
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
