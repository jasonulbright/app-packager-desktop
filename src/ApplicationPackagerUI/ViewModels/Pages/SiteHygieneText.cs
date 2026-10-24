using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
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
    /// <summary>The findings Prepare left out, one line each with the reason the service gave; at most <paramref name="max"/> lines.</summary>
    public static IReadOnlyList<string> DroppedLines(HygieneChange prepared, int max = 20)
    {
        var lines = prepared.Dropped.Take(max).Select(d => $"Left out: {Target(d.Finding)}. {SecretMask.Apply(d.Message)}").ToList();
        if (prepared.Dropped.Count > max) lines.Add($"And {SiteHygieneLabels.Count(prepared.Dropped.Count - max, "more finding")} left out.");
        return lines;
    }

    public static (SiteHygienePlan? Plan, string? Problem) BuildPlan(HygieneChange prepared)
    {
        var blocking = prepared.Checks.Where(Blocks).ToList();
        if (blocking.Count > 0)
            return (null, string.Join(" ", new[] { "The plan cannot run. Nothing was changed." }
                .Concat(blocking.Select(c => c.Message.Length > 0 ? SecretMask.Apply(c.Message) : $"{c.Name}: {c.Result}."))
                .Concat(DroppedLines(prepared, max: 3))));
        if (prepared.Findings.Count == 0)
            return (null, string.Join(" ", new[] { NothingLeft }.Concat(DroppedLines(prepared, max: 3)).Concat(prepared.Impact)));
        if (prepared.Findings.Any(f => f.FixKind != prepared.Kind))
            return (null, "The plan cannot run: the service returned a finding of another fix kind. Nothing was changed.");

        var kind = prepared.Kind;
        var delete = SiteHygieneLabels.IsDelete(kind);
        var action = SiteHygieneLabels.FixLabel(kind);
        var byKey = prepared.Findings.GroupBy(SiteHygieneLabels.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var entries = new List<SiteHygienePlanEntry>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in prepared.Targets)
        {
            if (!byKey.TryGetValue(t.FindingKey.ToUpperInvariant(), out var f)) continue;
            named.Add(SiteHygieneLabels.Key(f));
            var detail = t.Detail.Length > 0 ? $"{f.CheckId}: {t.Detail}" : $"{f.CheckId}: {FindingGridRow.FirstLine(f.Evidence)}";
            entries.Add(new SiteHygienePlanEntry(new ChangePlanItem(action, $"{t.ObjectType} {t.ObjectName} ({t.ObjectKey})", detail), f) { Target = t });
        }
        // A finding the service named no object for still gets its line, so every finding of the change shows.
        foreach (var f in prepared.Findings.Where(f => !named.Contains(SiteHygieneLabels.Key(f))))
            entries.Add(new SiteHygienePlanEntry(new ChangePlanItem(action, Target(f), $"{f.CheckId}: {FindingGridRow.FirstLine(f.Evidence)}"), f));

        // The notes box shows the first lines without a scroll, so the checks that need attention come first.
        var notes = new List<string>();
        notes.AddRange(prepared.Checks.Where(c => !IsPass(c)).Select(CheckLine));
        notes.AddRange(prepared.Impact);
        notes.AddRange(DroppedLines(prepared));
        notes.Add(delete ? DeleteNote : ChangeNote);
        notes.AddRange(prepared.Checks.Where(IsPass).Select(CheckLine));

        string? body = null;
        if (!string.IsNullOrWhiteSpace(prepared.FixText)) body = prepared.FixText;
        else
        {
            var texts = prepared.Findings.Select(f => f.FixText).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (texts.Count > 0) body = string.Join(Environment.NewLine, texts);
        }
        if (body is not null) notes.Add(BodyNote);

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
