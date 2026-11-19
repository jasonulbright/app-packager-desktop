namespace ApplicationPackagerUI.Services;

/// <summary>
/// The one log line that closes Check Latest or Check ConfigMgr: how many of the checked products need an update.
/// A product needs an update when its status starts with "Update available", the rule of <see cref="CveRowState"/>.
/// A product whose check failed has the status "Error"; it is not eligible and is named in the line.
/// The text is the text of the PowerShell app.
/// </summary>
public static class CheckResultSummary
{
    /// <param name="check">The name of the check, for example "Check ConfigMgr".</param>
    /// <param name="statuses">The status of each checked product after the check.</param>
    /// <param name="selected">The count of selected products after the check; null when the check does not select.</param>
    public static string Line(string check, IReadOnlyCollection<string?> statuses, int? selected)
    {
        var eligible = statuses.Count(CveRowState.IsUpdateAvailable);
        var failed = statuses.Count(s => string.Equals(s, "Error", StringComparison.Ordinal));
        var products = statuses.Count == 1 ? "product" : "products";
        var verb = eligible == 1 || statuses.Count == 1 ? "needs" : "need";
        var line = $"{check} finished: {eligible} of {statuses.Count} {products} {verb} an update";
        if (selected is not null) line += $"; {selected} selected";
        if (failed > 0) line += $"; {failed} {(failed == 1 ? "check" : "checks")} failed";
        return line + ".";
    }
}
