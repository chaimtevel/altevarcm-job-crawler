using Crawler.Domain.Jobs;

namespace Crawler.Tests.Domain;

public class StatusReasonsTests
{
    [Fact]
    public void Reason_with_a_long_redirect_url_is_cut_to_the_column_length()
    {
        var reason = $"Redirects off-site to https://sso.example.org/login?SAMLRequest={new string('a', 2500)}";

        var fitted = StatusReasons.Fit(reason)!;

        Assert.Equal(StatusReasons.MaxLength, fitted.Length);
        Assert.EndsWith("…", fitted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("External link")]
    public void Short_or_missing_reasons_are_unchanged(string? reason)
    {
        Assert.Equal(reason, StatusReasons.Fit(reason));
    }
}
