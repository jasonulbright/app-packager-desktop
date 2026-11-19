namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Shipped definitions that were removed, with the definition that replaces each. A setting that names the removed id
/// (hidden, tracked by One Click, a cadence or command override, a check state) moves to the replacement when the settings load,
/// and a command-line request for the removed name stops with the reason. Nothing that names a removed definition vanishes silently.
/// </summary>
public static class RemovedDefinitions
{
    public sealed record Entry(string Id, string Name, string ReplacementId, string ReplacementName, string Message);

    public static readonly IReadOnlyList<Entry> All =
    [
        new("000004e27e9117b0", "powershell7lts", "0000089d8d25e2a0", "powershell7",
            "This definition was removed: PowerShell 7.4 is out of support (Microsoft Learn, PowerShell Support Lifecycle: ended 10 Nov 2026). Use PowerShell 7 LTS.")
    ];

    public static Entry? ForId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(e => e.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The entry for a definition name, a file name or an application id such as catalog:powershell7lts.</summary>
    public static Entry? ForName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var name = text.Trim();
        var colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        name = Path.GetFileNameWithoutExtension(name);
        return All.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The reason a request for this application text is refused, or null when the text names no removed definition.</summary>
    public static string? RefusalFor(string? text) => ForName(text)?.Message;

    /// <summary>The ids with every removed id replaced by its replacement, without repeats. <paramref name="used"/> receives each entry that applied.</summary>
    public static string[] ReplaceIds(IEnumerable<string>? ids, ICollection<Entry> used)
    {
        var result = new List<string>();
        foreach (var id in ids ?? [])
        {
            var entry = ForId(id);
            var mapped = entry is null ? id : entry.ReplacementId;
            if (entry is not null && !used.Contains(entry)) used.Add(entry);
            if (!result.Contains(mapped, StringComparer.OrdinalIgnoreCase)) result.Add(mapped);
        }
        return result.ToArray();
    }

    /// <summary>
    /// The dictionary with every key that is a removed id moved to the replacement id. A value already stored for the replacement stays;
    /// otherwise the value of the removed id moves. The comparer of the input is kept.
    /// </summary>
    public static Dictionary<string, T> ReplaceKeys<T>(Dictionary<string, T>? source, ICollection<Entry> used)
    {
        var result = new Dictionary<string, T>(source?.Comparer ?? StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;
        foreach (var (key, value) in source)
            if (ForId(key) is null) result[key] = value;
        foreach (var (key, value) in source)
        {
            if (ForId(key) is not { } entry) continue;
            if (!used.Contains(entry)) used.Add(entry);
            if (!result.ContainsKey(entry.ReplacementId)) result[entry.ReplacementId] = value;
        }
        return result;
    }

    /// <summary>The notice that tells the operator which settings moved and why.</summary>
    public static string NoticeFor(IEnumerable<Entry> used)
    {
        var parts = used.Select(e => $"The settings that named the definition {e.Name} ({e.Id}) now name {e.ReplacementName} ({e.ReplacementId}). {e.Message}");
        return string.Join(" ", parts);
    }
}
