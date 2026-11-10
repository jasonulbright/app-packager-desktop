using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Runs test code on a new STA thread and rethrows its failure on the calling thread.</summary>
internal static class StaTestHelper
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>Runs every queued dispatcher operation down to the idle priority.</summary>
    public static void Flush() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Runs dispatcher operations until the condition holds; throws after the timeout (sixty seconds by default; a test returns as soon as the condition holds, and a busy thread pool of a full run can need more than ten).</summary>
    public static void PumpUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The condition did not hold within {(timeout ?? TimeSpan.FromSeconds(60)).TotalSeconds:0} seconds.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Flush();
    }
}
