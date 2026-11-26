using ApplicationPackagerEngine.Services.Vulnerability;

namespace ApplicationPackagerUI.Models;

/// <summary>The text of the CVE check column.</summary>
public static class CveCheck
{
    public const string NotChecked = "Not checked";
    public const string NoSiteVersion = "No site version";

    /// <param name="count">The CVE count cell of the row: a number, the reason a lookup was skipped, or empty before a lookup.</param>
    public static string Text(string? count, string? cpe)
    {
        var value = (count ?? "").Trim();
        if (value.Length == 0) return string.IsNullOrWhiteSpace(cpe) ? VulnerabilityGridCells.CpeMissing : NotChecked;
        if (value.All(char.IsAsciiDigit)) return value + " found";
        return value == "No ConfigMgr version" ? NoSiteVersion : value;
    }
}
