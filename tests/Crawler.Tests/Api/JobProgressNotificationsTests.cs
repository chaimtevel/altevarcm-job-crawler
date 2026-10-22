using Crawler.Api.Jobs;

namespace Crawler.Tests.Api;

public class JobProgressNotificationsTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Burst_of_changes_collapses_into_one_signal()
    {
        var notifications = new JobProgressNotifications();
        var jobId = Guid.NewGuid();
        using var subscription = notifications.Subscribe(jobId);

        notifications.Publish(jobId);
        notifications.Publish(jobId);
        notifications.Publish(jobId);

        Assert.True(await subscription.WaitAsync(Short, CancellationToken.None));
        Assert.False(await subscription.WaitAsync(Short, CancellationToken.None));
    }

    [Fact]
    public async Task Only_watchers_of_that_job_are_signalled()
    {
        var notifications = new JobProgressNotifications();
        var watchedJob = Guid.NewGuid();
        using var subscription = notifications.Subscribe(watchedJob);

        notifications.Publish(Guid.NewGuid());

        Assert.False(await subscription.WaitAsync(Short, CancellationToken.None));
    }

    [Fact]
    public async Task Disposed_subscription_stops_receiving()
    {
        var notifications = new JobProgressNotifications();
        var jobId = Guid.NewGuid();
        var subscription = notifications.Subscribe(jobId);
        subscription.Dispose();

        notifications.Publish(jobId);

        Assert.False(await subscription.WaitAsync(Short, CancellationToken.None));
    }
}
