using System.Threading.Channels;
using Crawler.Infrastructure.Persistence;
using Npgsql;

namespace Crawler.Api.Jobs;

/// <summary>Fans out "job changed" signals from Postgres to the SSE streams watching that job.</summary>
public class JobProgressNotifications
{
    // One lock keeps subscribe, unsubscribe and publish consistent; all three are tiny in-memory operations.
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HashSet<Channel<bool>>> _watchers = new();

    /// <summary>A signal per change; bursts collapse into one pending signal, so slow streams never back up.</summary>
    public Subscription Subscribe(Guid jobId)
    {
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        lock (_gate)
        {
            if (!_watchers.TryGetValue(jobId, out var channels))
            {
                _watchers[jobId] = channels = [];
            }

            channels.Add(channel);
        }

        return new Subscription(channel.Reader, () => Unsubscribe(jobId, channel));
    }

    public void Publish(Guid jobId)
    {
        lock (_gate)
        {
            if (!_watchers.TryGetValue(jobId, out var channels))
            {
                return;
            }

            foreach (var channel in channels)
            {
                channel.Writer.TryWrite(true);
            }
        }
    }

    private void Unsubscribe(Guid jobId, Channel<bool> channel)
    {
        lock (_gate)
        {
            if (_watchers.TryGetValue(jobId, out var channels) && channels.Remove(channel) && channels.Count == 0)
            {
                _watchers.Remove(jobId);
            }
        }
    }

    public sealed class Subscription(ChannelReader<bool> reader, Action unsubscribe) : IDisposable
    {
        /// <summary>True when the job changed; false when the wait timed out (time for a keep-alive).</summary>
        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(timeout);
            try
            {
                return await reader.ReadAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        public void Dispose() => unsubscribe();
    }
}

/// <summary>Holds one Postgres connection that LISTENs for job changes; reconnects if it drops.</summary>
public class JobProgressListener(
    IConfiguration configuration,
    JobProgressNotifications notifications,
    ILogger<JobProgressListener> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, e) =>
                {
                    if (Guid.TryParse(e.Payload, out var jobId))
                    {
                        notifications.Publish(jobId);
                    }
                };

                await using (var listen = new NpgsqlCommand($"LISTEN {CrawlStore.ProgressChannel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                logger.LogInformation("Listening for job progress on Postgres channel {Channel}", CrawlStore.ProgressChannel);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Open streams fall back to their keep-alive refresh until the listener is back.
                logger.LogWarning(ex, "Job progress listener lost its connection; retrying in {Delay}s", ReconnectDelay.TotalSeconds);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }
}
