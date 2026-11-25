using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Helpers;

/// <summary>One line of the detail pane: a label and its text.</summary>
public sealed record DetailLine(string Label, string Text);

/// <summary>
/// The text of the detail pane for the focused row (overhaul V1, phase 4). Pure, so a test reads it without a window. An empty value reads "Not known"
/// or "None" rather than a blank, so a screen reader always has a word.
/// </summary>
public static class PackagesDetail
{
    public const string NotStaged = "Not staged";

    public static IReadOnlyList<DetailLine> Lines(PackagerInfo row, string stageText) =>
    [
        new("Vendor", Or(row.Vendor, "Not known")),
        new("Status", Or(row.Status, "Not known")),
        new("ConfigMgr version", Or(row.CurrentVersion, "Not known")),
        new("Latest version", Or(row.LatestVersion, "Not known")),
        new("CVEs", Or(row.CveCount, "Not checked")),
        new("Stage", Or(stageText, NotStaged))
    ];

    /// <summary>The stage state from the version of the stage manifest the row has, or "Not staged".</summary>
    public static string StageText(bool hasManifest, string? version) =>
        !hasManifest ? NotStaged : string.IsNullOrWhiteSpace(version) ? "Staged" : "Staged " + version;

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
