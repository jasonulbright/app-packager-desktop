using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>One line of the log. Two lines with the same text are two entries, so a list can select and scroll to each one.</summary>
public sealed class LogEntry(string text)
{
    public string Text { get; } = text;

    public override string ToString() => Text;
}

/// <summary>
/// The lines of the log that the window shows. It keeps the newest <see cref="Limit"/> lines; every line is also written to the log files,
/// which are the full record. A view binds to it, so it changes only on the thread that made it, when that thread has a dispatcher.
/// </summary>
public sealed class OperationLog : ObservableCollection<LogEntry>
{
    private readonly Lock _gate = new();
    public const int DefaultLimit = 2000;

    public OperationLog(int limit = DefaultLimit) => Limit = Math.Max(1, limit);

    public int Limit { get; }

    /// <summary>The dispatcher of the thread that made the log; null for a thread with none.</summary>
    public Dispatcher? Owner { get; } = Dispatcher.FromThread(Thread.CurrentThread);

    /// <summary>True when this thread must hand its change to the <see cref="Owner"/>.</summary>
    public bool OnAnotherThread => Owner is { HasShutdownStarted: false } owner && !owner.CheckAccess();

    /// <summary>Adds one line and drops the oldest lines past the limit.</summary>
    public void Append(string line)
    {
        if (OnAnotherThread)
        {
            Owner!.BeginInvoke(() => Append(line));
            return;
        }
        lock (_gate)
        {
            while (Count >= Limit) RemoveAt(0);
            Add(new LogEntry(line));
        }
    }

    /// <summary>The kept lines as one text.</summary>
    public string Text
    {
        get { lock (_gate) return string.Join(Environment.NewLine, this); }
    }

    public string Last
    {
        get { lock (_gate) return Count > 0 ? this[Count - 1].Text : ""; }
    }
}
