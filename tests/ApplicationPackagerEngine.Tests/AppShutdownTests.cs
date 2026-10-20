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
        AppShutdown.Complete(new FakeHost(steps, _ => Task.Delay(Timeout.Infinite, CancellationToken.None)),
            TimeSpan.FromMilliseconds(100), () => steps.Add("exit"));
        Assert.Equal(["stop", "dispose", "exit"], steps);
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
