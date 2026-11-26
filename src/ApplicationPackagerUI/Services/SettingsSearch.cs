using System.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>One result of the settings search: a page, or one field of a page.</summary>
public sealed class SettingsSearchEntry
{
    public required string PageId { get; init; }
    public required string Title { get; init; }

    /// <summary>The names of the page in the tree, from the top, for example "Connections > WSUS".</summary>
    public required string Path { get; init; }

    public string Keywords { get; init; } = "";

    /// <summary>
    /// The control of a field, asked for after its page shows, because a row of a list is built only then. Null for the result of a page.
    /// </summary>
    public Func<FrameworkElement?>? Locate { get; init; }

    /// <summary>Why the field is unavailable now; null when it is available or the app has no text for it.</summary>
    public Func<string?> Unavailable { get; init; } = () => null;

    /// <summary>The words that a query is matched against.</summary>
    public string Text => (Locate is null ? $"{Path} {Keywords}" : $"{Title} {Keywords} {Path}").ToLowerInvariant();

    public string ResultText => $"{Title}   -   {Path}";
}

/// <summary>The matching rules of the Options search of the PowerShell app (Search-OptionsIndex).</summary>
public static class SettingsSearch
{
    public const int Limit = 40;
    public const string NoResultText = "No setting found.";

    private static string[] Words(string? query) =>
        (query ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Every entry whose text holds every word of the query. The order: a title equal to the query, a title that starts with the first word, a title
    /// that holds the first word, then the rest; each group by title. All matches are returned; the page shows the first <see cref="Limit"/>.
    /// </summary>
    public static IReadOnlyList<SettingsSearchEntry> Matches(IEnumerable<SettingsSearchEntry> index, string? query)
    {
        var words = Words(query);
        if (words.Length == 0) return [];
        var joined = string.Join(' ', words);
        int Rank(SettingsSearchEntry entry)
        {
            var title = entry.Title.ToLowerInvariant();
            if (title == joined) return 0;
            if (title.StartsWith(words[0], StringComparison.Ordinal)) return 1;
            return title.Contains(words[0], StringComparison.Ordinal) ? 2 : 3;
        }

        return index.Where(e => { var text = e.Text; return words.All(w => text.Contains(w, StringComparison.Ordinal)); })
            .OrderBy(Rank).ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
