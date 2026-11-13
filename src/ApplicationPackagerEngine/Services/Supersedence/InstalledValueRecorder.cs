using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// Records what an administrator read from the installed product of one title and version (the Exact guard compares it with the expected
/// value of a version clause), and the acceptance of a block of the guard for one title and version. Both are stored per title and per
/// version: a new version needs its own value or its own acceptance.
/// </summary>
public static partial class InstalledValueRecorder
{
    [GeneratedRegex(@"^[0-9][0-9A-Za-z.+_-]{0,63}\z")]
    private static partial Regex ValueGrammar();

    /// <summary>True for the text of a version or an installed value: starts with a digit, letters, digits and . + _ - only, at most 64 characters.</summary>
    public static bool IsValidValue(string? text) => text is not null && ValueGrammar().IsMatch(text);

    /// <summary>True for the version of a title: 1 to 64 characters, no control character, no "|" (the key separator), no leading or trailing space.</summary>
    public static bool IsValidVersionText(string? text) =>
        text is not null && text.Length is > 0 and <= 64 && text == text.Trim() && !text.Any(c => char.IsControl(c) || c == '|');

    private static string Key(string titleKey, string version)
    {
        if (string.IsNullOrWhiteSpace(titleKey))
            throw new InvalidOperationException("The title has no identifier, so nothing can be recorded for it.");
        if (!IsValidVersionText(version))
            throw new InvalidOperationException($"The version '{version}' is empty, longer than 64 characters, or holds a control character, a space at an end, or '|'.");
        return ExactGuard.RecordKey(titleKey, version);
    }

    /// <summary>Stores the installed value of a title and a version; returns the key. A value that is not a version text is refused.</summary>
    public static string Record(SupersedenceSettings settings, string titleKey, string version, string installedValue)
    {
        var key = Key(titleKey, version);
        if (!IsValidValue(installedValue))
            throw new InvalidOperationException($"The installed value '{installedValue}' is not a version (it starts with a digit and holds letters, digits and . + _ - only).");
        // A file that was read back holds a case-sensitive dictionary; rebuild it so that two keys never differ only in case.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.InstalledValues ?? []) values[pair.Key] = pair.Value;
        values[key] = installedValue;
        settings.InstalledValues = values;
        return key;
    }

    /// <summary>Accepts the block of the Exact guard for a title and a version; the clause is then written as Minimum. Returns the key.</summary>
    public static string AcceptBlock(SupersedenceSettings settings, string titleKey, string version)
    {
        var key = Key(titleKey, version);
        settings.AcceptedExactBlocks ??= [];
        if (!settings.AcceptedExactBlocks.Contains(key, StringComparer.OrdinalIgnoreCase)) settings.AcceptedExactBlocks.Add(key);
        return key;
    }
}
