using System.Collections.ObjectModel;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The lines of the log that the window shows. It keeps the newest <see cref="Limit"/> lines; every line is also written to the log files,
/// which are the full record.
/// </summary>
public sealed class OperationLog : ObservableCollection<string>
{
    public const int DefaultLimit = 2000;

    public OperationLog(int limit = DefaultLimit) => Limit = Math.Max(1, limit);

    public int Limit { get; }

    /// <summary>Adds one line and drops the oldest lines past the limit.</summary>
    public void Append(string line)
    {
        while (Count >= Limit) RemoveAt(0);
        Add(line);
    }

    /// <summary>The kept lines as one text.</summary>
    public string Text => string.Join(Environment.NewLine, this);

    public string Last => Count > 0 ? this[Count - 1] : "";
}
