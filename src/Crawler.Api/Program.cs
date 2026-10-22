using System.Text.Json.Serialization;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Crawler.Api.Jobs;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCrawlerPersistence(builder.Configuration);
// Publishes through the Dapr sidecar at DAPR_HTTP_ENDPOINT/DAPR_GRPC_ENDPOINT (localhost when unset).
builder.Services.AddDaprClient();
builder.Services.AddScoped<CrawlPagePublisher>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CrawlerDbContext>("postgres")
    .AddDapr();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Demo convenience: migrations run at startup. In production they'd run as a separate deploy step.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<CrawlerDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// Serves the built React UI from wwwroot (copied in by the Docker build).
app.UseDefaultFiles();
app.UseStaticFiles();

// Swagger is enabled in every environment because this is a demo/assessment service.
app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
app.MapJobEndpoints();
// Client-side routes (/jobs/..., /history) load the SPA on GET; files and /api routes don't.
app.MapFallbackToFile("{*path:nonfile:regex(^(?!api(/|$)).*$)}", "index.html")
    .WithMetadata(new HttpMethodMetadata(["GET", "HEAD"]));

app.Run();
