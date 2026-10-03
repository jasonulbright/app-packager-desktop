namespace ApplicationPackagerUI.Services;

/// <summary>
/// Delivers a run's output line to its sink: through the WPF dispatcher when an
/// application object exists, else on the calling thread. A process that never
/// creates a System.Windows.Application has no dispatcher to post to.
/// </summary>
public static class OutputLineDispatch
{
    public static void Post(Action<string>? sink, string line)
    {
        if (sink is null) return;
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher) dispatcher.InvokeAsync(() => sink(line));
        else sink(line);
    }

    public static async Task SendAsync(Action<string>? sink, string line)
    {
        if (sink is null) return;
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher) await dispatcher.InvokeAsync(() => sink(line));
        else sink(line);
    }
}
