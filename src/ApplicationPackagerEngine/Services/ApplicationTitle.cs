using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// How the version appears in the application title a package run creates.
/// </summary>
public enum TitleMode
{
    /// <summary>The title the definition produces, unchanged.</summary>
    Default,

    /// <summary>The base title with this release's version appended, so each release is its own application.</summary>
    IncludeVersion,

    /// <summary>The base title alone, so one application carries every release.</summary>
    NoVersion
}

/// <summary>
/// Resolves the application title a package run uses for the MECM application
/// and the Intune display name. Content folders and detection stay versioned.
/// </summary>
public static partial class ApplicationTitle
{
    public const string IncludeVersionToken = "IncludeVersion";
    public const string NoVersionToken = "NoVersion";

    [GeneratedRegex(@"\(\s*\)")]
    private static partial Regex EmptyParentheses();

    [GeneratedRegex(@"\s+-\s+(?=\(|$)")]
    private static partial Regex DanglingSeparator();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedSpace();

    /// <summary>Reads a stored token. Anything unrecognized is the packager default.</summary>
    public static TitleMode Parse(string? stored) => stored switch
    {
        IncludeVersionToken => TitleMode.IncludeVersion,
        NoVersionToken => TitleMode.NoVersion,
        _ => TitleMode.Default
    };

    /// <summary>
    /// The title mode for one package run, most specific first: the profile's mode, the
    /// per-application entry, the global IncludeVersionInTitle, the global
    /// RemoveVersionFromTitle, then the packager default. A tier that stores no mode
    /// passes the decision to the next one.
    /// </summary>
    public static TitleMode ResolveMode(string? profileMode, string? perApplicationMode,
        bool includeVersionInTitle, bool removeVersionFromTitle)
    {
        foreach (var stored in new[] { profileMode, perApplicationMode })
        {
            var mode = Parse(Workbench.PackagerChildBridge.NormalizeTitleMode(stored?.Trim()));
            if (mode != TitleMode.Default) return mode;
        }
        if (includeVersionInTitle) return TitleMode.IncludeVersion;
        if (removeVersionFromTitle) return TitleMode.NoVersion;
        return TitleMode.Default;
    }

    /// <summary>The global default mode token alone, for a run with no profile or per-application entry.</summary>
    public static string DefaultModeToken(bool includeVersionInTitle, bool removeVersionFromTitle) =>
        ToToken(ResolveMode(null, null, includeVersionInTitle, removeVersionFromTitle));

    /// <summary>Writes the stored token for a mode. Default stores nothing.</summary>
    public static string ToToken(TitleMode mode) => mode switch
    {
        TitleMode.IncludeVersion => IncludeVersionToken,
        TitleMode.NoVersion => NoVersionToken,
        _ => ""
    };

    /// <summary>
    /// Resolves the title for one release. Only this release's exact version
    /// token is removed from the name, so product years, architecture,
    /// language and channel qualifiers survive. A title that is nothing but
    /// its version is refused: the result would collide with every release.
    /// </summary>
    public static string Resolve(string appName, string version, TitleMode mode)
    {
        if (mode == TitleMode.Default) return appName;

        var baseName = appName;
        if (!string.IsNullOrWhiteSpace(version))
        {
            var pattern = @"(?<![\w.])v?" + Regex.Escape(version) + @"(?![\w.])";
            // Strip-then-append would move a version the name already carries to the
            // end: "Audacity 3.7.9 (x64)" must not become "Audacity (x64) - 3.7.9".
            if (mode == TitleMode.IncludeVersion && Regex.IsMatch(appName, pattern, RegexOptions.IgnoreCase))
                return appName;
            baseName = Regex.Replace(baseName, pattern, "", RegexOptions.IgnoreCase);
        }

        baseName = EmptyParentheses().Replace(baseName, "");
        baseName = DanglingSeparator().Replace(baseName, " ");
        baseName = RepeatedSpace().Replace(baseName, " ");
        baseName = baseName.Trim().TrimEnd('-').Trim();

        if (baseName.Length == 0)
            throw new InvalidOperationException("Application title cannot consist only of its version.");

        return mode == TitleMode.IncludeVersion ? $"{baseName} - {version}" : baseName;
    }
}
