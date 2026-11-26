using System.Globalization;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Models;
using Wpf.Ui.Controls;

namespace ApplicationPackagerUI.Helpers;

/// <summary>One line of the detail pane: a label and its text. The status line also has the color class of the status, which picks its glyph.</summary>
public sealed record DetailLine(string Label, string Text, string Category = "")
{
    public bool HasGlyph => Category.Length > 0;

    /// <summary>The same glyph as the Status column, so the state never shows by color alone.</summary>
    public SymbolRegular Glyph => Category switch
    {
        nameof(StatusCategory.Success) => SymbolRegular.CheckmarkCircle24,
        nameof(StatusCategory.Warning) => SymbolRegular.Warning24,
        nameof(StatusCategory.Info) => SymbolRegular.Info24,
        nameof(StatusCategory.Error) => SymbolRegular.DismissCircle24,
        _ => SymbolRegular.Circle24
    };
}

/// <summary>
/// The text of the detail pane for the focused row (overhaul V1, phase 4). Pure, so a test reads it without a window. An empty value reads "Not known"
/// or "None" rather than a blank, so a screen reader always has a word.
/// </summary>
public static class PackagesDetail
{
    public const string NotStaged = "Not staged";
    public const string CheckingStage = "Checking the stage folder";
    public const string NotFoundCheck = "Not found check";

    public static IReadOnlyList<DetailLine> Lines(PackagerInfo row, string stageText, DateTimeOffset? now = null)
    {
        var lines = new List<DetailLine>
        {
            new("Vendor", Or(row.Vendor, "Not known")),
            new("Status", Or(row.Status, "Not known"), new StatusCategoryConverter().Convert(row.Status, typeof(string), null, CultureInfo.InvariantCulture)?.ToString() ?? "Neutral"),
            new("ConfigMgr version", Or(row.CurrentVersion, "Not known")),
            new("Latest version", Or(row.LatestVersion, "Not known")),
            new("CVEs", Or(row.CveCount, "Not checked")),
            new("Stage", Or(stageText, NotStaged))
        };
        // "Not found" is the result of a check that can be old, so the pane says when it ran.
        if (row.Status == PackagesTab.NotFoundStatus)
            lines.Insert(2, new(NotFoundCheck, row.SiteCheckedAt is { } at ? Age(at, now ?? DateTimeOffset.Now) : "The time of the check is not stored."));
        return lines;
    }

    /// <summary>"Checked today, 2026-11-26 14:05." or "Checked 3 days ago, 2026-11-23 09:00."</summary>
    public static string Age(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (now.ToLocalTime().Date - at.ToLocalTime().Date).Days;
        var when = at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return days <= 0 ? $"Checked today, {when}." : $"Checked {days} {(days == 1 ? "day" : "days")} ago, {when}.";
    }

    /// <summary>"ConfigMgr \u00b7 Site MCM. Intune and WSUS are off." The systems that Publish All reaches, and the ones that are off.</summary>
    public static string PublishingText(SystemsInUse? systems, string? siteCode)
    {
        var set = ScopeText.SetOf(systems);
        var on = set.Any ? set.Describe() : "No system is in use";
        if (set.ConfigMgr && !string.IsNullOrWhiteSpace(siteCode)) on += " \u00b7 Site " + siteCode.Trim();
        var off = new[] { (set.ConfigMgr, "ConfigMgr"), (set.Intune, "Intune"), (set.Wsus, "WSUS") }.Where(x => !x.Item1).Select(x => x.Item2).ToList();
        var offText = off.Count == 0 || !set.Any ? ""
            : off.Count == 1 ? $" {off[0]} is off."
            : $" {string.Join(", ", off.Take(off.Count - 1))} and {off[^1]} are off.";
        return $"{on}.{offText}";
    }

    /// <summary>The stage state from the version of the stage manifest the row has, or "Not staged".</summary>
    public static string StageText(bool hasManifest, string? version) =>
        !hasManifest ? NotStaged : string.IsNullOrWhiteSpace(version) ? "Staged" : "Staged " + version;

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
