using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Crawling;
using Microsoft.Extensions.Http.Resilience;
using Polly;

// The Dapr sidecar delivers pub/sub messages to the worker over HTTP, so it is an ASP.NET Core app.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCrawlerPersistence(builder.Configuration);
builder.Services.AddDaprClient();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CrawlStore>();
builder.Services.AddScoped<CrawlPageHandler>();
builder.Services.AddHostedService<JobTimeoutService>();

// Only for tests against local fixtures; never enable in a deployed environment.
var allowPrivateNetworks = builder.Configuration.GetValue<bool>("Crawl:AllowPrivateNetworks");

builder.Services
    .AddHttpClient<PageFetcher>(client => client.DefaultRequestHeaders.UserAgent.ParseAdd("CrawlerBot/1.0"))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxAutomaticRedirections = 5,
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
app.MapSubscribeHandler();

app.MapPost("/messages/crawl-page", async (CrawlPageMessage message, CrawlPageHandler handler, CancellationToken ct) =>
    {
        await handler.HandleAsync(message, ct);
        return Results.Ok();
    })
    .WithTopic(CrawlMessaging.PubSubName, CrawlMessaging.CrawlPageTopic);

app.MapHealthChecks("/health");

app.Run();
