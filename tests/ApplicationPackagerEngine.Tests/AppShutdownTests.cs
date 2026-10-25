using ApplicationPackagerUI.Services;
using Microsoft.Extensions.Hosting;

namespace ApplicationPackagerEngine.Tests;

public class AppShutdownTests
{
    private sealed class FakeHost(List<string> steps, Func<CancellationToken, Task>? stop = null, bool disposeThrows = false) : IHost
    {
        public IServiceProvider Services => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            steps.Add("stop");
            return stop?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public void Dispose()
        {
            steps.Add("dispose");
            if (disposeThrows) throw new InvalidOperationException("dispose failed");
        }
    }

    [Fact]
    public void TheProcessEndsAfterTheHostStopsAndIsDisposed()
    {
        var steps = new List<string>();
        AppShutdown.Complete(new FakeHost(steps), TimeSpan.FromSeconds(5), () => steps.Add("exit"));
        Assert.Equal(["stop", "dispose", "exit"], steps);
    }

    [Fact]
    public void TheProcessEndsWhenTheStopNeverFinishes()
    {
        var steps = new List<string>();
        var timeout = TimeSpan.FromMilliseconds(100);
        var never = new TaskCompletionSource();
        using var stopStarted = new ManualResetEventSlim();
        Task? waitedFor = null;
        TimeSpan? waitLimit = null;
        AppShutdown.Complete(new FakeHost(steps, _ =>
            {
                stopStarted.Set();
                return never.Task;
            }),
            timeout, () => steps.Add("exit"),
            (running, limit) =>
            {
                Assert.True(stopStarted.Wait(TimeSpan.FromSeconds(30)), "The stop did not start.");
                waitedFor = running;
                waitLimit = limit;
                return running.Wait(TimeSpan.Zero);
            });
        Assert.Equal(["stop", "dispose", "exit"], steps);
        Assert.Equal(timeout, waitLimit);
        Assert.False(waitedFor!.IsCompleted);
    }

    [Fact]
    public void TheStopRunsOffTheThreadPool()
    {
        bool? onPool = null;
        Assert.True(HostShutdown.StopAndWait(_ =>
        {
            onPool = Thread.CurrentThread.IsThreadPoolThread;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(30)));
        Assert.False(onPool);
    }

    [Fact]
    public void TheProcessEndsWhenTheDisposeThrows()
    {
        var steps = new List<string>();
        Assert.Throws<InvalidOperationException>(() =>
            AppShutdown.Complete(new FakeHost(steps, disposeThrows: true), TimeSpan.FromSeconds(5), () => steps.Add("exit")));
        Assert.Equal(["stop", "dispose", "exit"], steps);
    }

    [Fact]
    public void TheProcessEndsWithoutAHost()
    {
        var exited = 0;
        AppShutdown.Complete(null, TimeSpan.FromSeconds(5), () => exited++);
        Assert.Equal(1, exited);
    }
}
