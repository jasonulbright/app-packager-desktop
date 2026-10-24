using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The plan text of the Site Hygiene page, made from the prepared change only.</summary>
public static class SiteHygieneText
{
    public const string Pass = "Pass";
    public const string Warn = "Warn";
    public const string BodyName = "Equivalent PowerShell";

    public const string NothingLeft =
        "No finding of the plan still holds: the objects changed since the scan. Nothing was changed. Scan again to see the current state.";

    public const string DeleteNote =
        "The objects are deleted one at a time, and each delete is read back. Before each delete the service checks again that the finding still holds and that nothing references the object; an object that changed is skipped.";

    public const string ChangeNote =
        "The settings are changed one object at a time, and each new value is read back. Before each change the service checks again that the finding still holds; an object that changed is skipped.";

    public const string BodyNote = "The PowerShell text shows what the page does. The page runs the typed fix, not this text.";

    public static string DeleteConfirmation(int count) =>
        $"Delete {SiteHygieneLabels.Count(count, "object")} from the site. A delete cannot be undone.";

    public const string DestructiveText = "I checked the list of objects above and the impact lines.";

    /// <summary>A check that stops the plan: any result other than Pass or Warn.</summary>
    public static bool Blocks(PreflightCheck check) =>
        !string.Equals(check.Result, Pass, StringComparison.OrdinalIgnoreCase) && !string.Equals(check.Result, Warn, StringComparison.OrdinalIgnoreCase);

    private static bool IsPass(PreflightCheck check) => string.Equals(check.Result, Pass, StringComparison.OrdinalIgnoreCase);

    private static string CheckLine(PreflightCheck c) => $"{c.Name}: {c.Result}{(c.Message.Length > 0 ? ". " + c.Message : ".")}";

    public static string Target(HygieneFinding finding) => $"{finding.ObjectType} {finding.ObjectName} ({finding.ObjectKey})";

    public static string Title(HygieneFixKind kind) => SiteHygieneLabels.IsDelete(kind)
        ? $"Site Hygiene: {SiteHygieneLabels.FixLabel(kind).ToLowerInvariant()} (cannot be undone)"
        : $"Site Hygiene: {SiteHygieneLabels.FixLabel(kind).ToLowerInvariant()}";

    /// <summary>
    /// The plan of a prepared change. Every line, the PowerShell text and the notes come from the
    /// findings, the impact and the checks the service returned at Prepare; the scan rows are not read.
    /// Null with the reason when the change cannot run.
    /// </summary>
    public static (SiteHygienePlan? Plan, string? Problem) BuildPlan(HygieneChange prepared)
    {
        var blocking = prepared.Checks.Where(Blocks).ToList();
        if (blocking.Count > 0)
            return (null, "The plan cannot run. Nothing was changed. " + string.Join(" ", blocking.Select(c => c.Message.Length > 0 ? c.Message : $"{c.Name}: {c.Result}.")));
        if (prepared.Findings.Count == 0)
            return (null, string.Join(" ", prepared.Impact.Prepend(NothingLeft)));
        if (prepared.Findings.Any(f => f.FixKind != prepared.Kind))
            return (null, "The plan cannot run: the service returned a finding of another fix kind. Nothing was changed.");

        var kind = prepared.Kind;
        var delete = SiteHygieneLabels.IsDelete(kind);
        var action = SiteHygieneLabels.FixLabel(kind);
        var entries = prepared.Findings
            .Select(f => new SiteHygienePlanEntry(new ChangePlanItem(action, Target(f), $"{f.CheckId}: {FindingGridRow.FirstLine(f.Evidence)}"), f))
            .ToList();

        // The notes box shows the first lines without a scroll, so the checks that need attention come first.
        var notes = new List<string>();
        notes.AddRange(prepared.Checks.Where(c => !IsPass(c)).Select(CheckLine));
        notes.AddRange(prepared.Impact);
        notes.Add(delete ? DeleteNote : ChangeNote);
        notes.AddRange(prepared.Checks.Where(IsPass).Select(CheckLine));

        var texts = prepared.Findings.Select(f => f.FixText).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        string? body = null;
        if (texts.Count > 0)
        {
            notes.Add(BodyNote);
            body = string.Join(Environment.NewLine, texts);
        }

        var count = entries.Count;
        var plan = new ChangePlan(Title(kind), $"{action}: {SiteHygieneLabels.Count(count, "object")}", entries.Select(e => e.Item), isDestructive: delete, hasUndo: false)
        {
            ExtraConfirmation = delete ? DeleteConfirmation(count) : null,
            DestructiveConfirmation = delete ? DestructiveText : null,
            Notes = notes,
            LogName = kind.ToString(),
            Body = body,
            BodyName = body is null ? null : BodyName
        };
        return (new SiteHygienePlan(action, prepared, plan, entries), null);
    }
}
