using Crawler.Infrastructure.Messaging;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Crawling;
using Microsoft.Extensions.Http.Resilience;
using Polly;

// The Dapr sidecar delivers pub/sub messages to the worker over HTTP, so it is an ASP.NET Core app.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCrawlerPersistence(builder.Configuration);
builder.Services.AddDaprClient();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CrawlerDbContext>("postgres")
    .AddDapr();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CrawlPagePublisher>();
builder.Services.AddScoped<CrawlPageHandler>();
builder.Services.AddScoped<SitemapReader>();
builder.Services.AddHostedService<JobTimeoutService>();

// Message endpoints accept only calls carrying the sidecar's APP_API_TOKEN.
const string DaprPolicy = "Dapr";
builder.Services.AddAuthentication().AddDapr();
builder.Services.AddAuthorization(options => options.AddDapr(DaprPolicy));

// Only for tests against local fixtures; never enable in a deployed environment.
var allowPrivateNetworks = builder.Configuration.GetValue<bool>("Crawl:AllowPrivateNetworks");

builder.Services
    .AddHttpClient<PageFetcher>(client => client.DefaultRequestHeaders.UserAgent.ParseAdd("CrawlerBot/1.0"))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // PageFetcher follows redirects itself, so one hop limit covers every case.
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = allowPrivateNetworks ? null : PublicNetworkConnector.ConnectAsync,
    })
    .AddResilienceHandler("page-fetch", pipeline =>
    {
        // Total budget, retries on 5xx/408/429/network errors, then a per-attempt timeout.
        pipeline.AddTimeout(TimeSpan.FromSeconds(30));
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(500),
        });
        pipeline.AddTimeout(TimeSpan.FromSeconds(10));
    });

var app = builder.Build();

// Unwraps CloudEvents envelopes and exposes /dapr/subscribe for topic-bound endpoints.
app.UseCloudEvents();
app.UseAuthentication();
app.UseAuthorization();
app.MapSubscribeHandler();

app.MapPost("/messages/crawl-page", async (CrawlPageMessage message, CrawlPageHandler handler, CancellationToken ct) =>
    {
        await handler.HandleAsync(message, ct);
        return Results.Ok();
    })
    .WithTopic(CrawlMessaging.PubSubName, CrawlMessaging.CrawlPageTopic)
    .RequireAuthorization(DaprPolicy);

app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });

app.Run();
