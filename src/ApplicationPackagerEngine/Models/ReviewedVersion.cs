namespace ApplicationPackagerEngine.Models;

/// <summary>
/// A run that follows a review publishes the version that the review showed and no other. A stage can hold another version than the review:
/// a stage reads the vendor again, and a local installer stages under its own file version.
/// </summary>
public static class ReviewedVersion
{
    /// <summary>
    /// Why the stage must not be published, or null when it can be.
    /// </summary>
    /// <param name="reviewed">The version that the review showed; empty when it showed none. Null for a run with no review (the command line).</param>
    /// <param name="staged">The version of the stage that the publish would use.</param>
    public static string? Refusal(string? reviewed, string? staged)
    {
        if (reviewed is null) return null;
        var of = string.IsNullOrWhiteSpace(staged) ? "no version" : staged.Trim();
        if (reviewed.Trim().Length > 0 && string.Equals(reviewed.Trim(), staged?.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        var shown = reviewed.Trim().Length > 0 ? reviewed.Trim() : "no version";
        return $"Staged {of}, reviewed {shown}; check the version and review again. Nothing was published.";
    }
}
