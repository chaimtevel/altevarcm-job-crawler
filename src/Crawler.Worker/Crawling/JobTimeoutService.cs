using Crawler.Infrastructure.Persistence;

namespace Crawler.Worker.Crawling;

/// <summary>Periodically fails jobs past their deadline, e.g. when expired messages never reach a worker.</summary>
public class JobTimeoutService(IServiceScopeFactory scopes, ILogger<JobTimeoutService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var timedOut = await scope.ServiceProvider.GetRequiredService<CrawlStore>().TimeOutExpiredJobsAsync(stoppingToken);
                if (timedOut > 0)
                {
                    logger.LogWarning("Timed out {Count} job(s) past their deadline", timedOut);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Job timeout sweep failed; retrying next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
