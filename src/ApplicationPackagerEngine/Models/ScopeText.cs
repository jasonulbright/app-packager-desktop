namespace ApplicationPackagerEngine.Models;

/// <summary>Where the systems of a publish come from.</summary>
public enum ScopeSource
{
    /// <summary>Settings, Systems in Use: a manual publish.</summary>
    SystemsInUse,
    /// <summary>One Click, the destinations saved for the application.</summary>
    SavedForApplication,
    /// <summary>One Click, the default destinations.</summary>
    Default,
    /// <summary>One Click, a box of the plan window differs from the saved choice.</summary>
    ChangedForRun
}

/// <summary>The words for the scope of a publish. The batch bar, the publish review and the One Click plan read them from here.</summary>
public static class ScopeText
{
    /// <summary>"ConfigMgr and Intune \u00b7 Site MCM \u00b7 from Settings, Systems in Use". The site shows only when ConfigMgr is in the set.</summary>
    public static string For(DestinationSet set, ScopeSource source, string? siteCode = null)
    {
        var parts = new List<string> { set.Any ? set.Describe() : "no system in use" };
        if (set.ConfigMgr && !string.IsNullOrWhiteSpace(siteCode)) parts.Add("Site " + siteCode.Trim());
        parts.Add(source == ScopeSource.SystemsInUse ? "from Settings, Systems in Use" : Words(source));
        return string.Join(" \u00b7 ", parts);
    }

    /// <summary>The set of Settings, Systems in Use.</summary>
    public static DestinationSet SetOf(SystemsInUse? systems) =>
        new(systems?.ConfigMgr ?? false, systems?.Wsus ?? false, systems?.Intune ?? false);

    /// <summary>"saved for this application", "default" or "changed for this run".</summary>
    public static string Words(ScopeSource source) => source switch
    {
        ScopeSource.SavedForApplication => "saved for this application",
        ScopeSource.Default => "default",
        ScopeSource.ChangedForRun => "changed for this run",
        _ => "from Settings, Systems in Use"
    };

    /// <summary>The words as the first words of a cell: "Saved for this application".</summary>
    public static string Cell(ScopeSource source) => char.ToUpperInvariant(Words(source)[0]) + Words(source)[1..];
}
