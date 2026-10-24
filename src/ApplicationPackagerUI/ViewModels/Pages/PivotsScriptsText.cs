using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The plan text of the Pivots and Scripts page, made from the prepared change only.</summary>
public static class PivotsScriptsText
{
    public const string Pass = "Pass";
    public const string Warn = "Warn";
    public const int MaxUnmatchedNamed = 20;

    public const string NoUndo = "A script run cannot be undone. There is no Undo; the audit log is the record.";
    public const string PivotNote = "The query runs on each client and returns data. It changes nothing on the client. It cannot be undone; the audit log is the record.";
    public const string StopWaitingNote = "Clients that received the request run it, also after Stop waiting.";
    public const string ScriptRunBox = "I read the script text, its hash and the parameters. A script run cannot be undone.";
    public const string RemoveNote = "The run history of the script goes with it. A removal cannot be undone.";
    public const string ApproveNote = "An approved script can run on clients from this page and from the console. By default the site refuses an approval by the author of the script.";
    public const string SubmitNote = "The site stores the script as Waiting for approval. It cannot run until it is approved.";
    public const string DenyNote = "A declined script cannot run. It can be approved later.";
    public const string NoDevices = "The targets resolve to no device. Nothing was sent.";
    public const string ScriptNotRead = "The script was not read again before the plan. Nothing was sent. Select Run again.";
    public const string TruncatedNote = "Truncated: more devices match than the plan holds. Narrow the target.";

    public static string Title(PivotsChangeKind kind) => kind switch
    {
        PivotsChangeKind.RunPivot => "Run a CMPivot query on devices",
        PivotsChangeKind.RunScript => "Run a script on devices",
        PivotsChangeKind.SubmitScript => "Submit a script to the site",
        PivotsChangeKind.ApproveScript => "Approve a site script",
        PivotsChangeKind.DenyScript => "Deny a site script",
        PivotsChangeKind.RemoveScript => "Remove a site script",
        _ => kind.ToString()
    };

    /// <summary>A check that stops the plan: a result other than Pass or Warn.</summary>
    public static bool Blocks(PreflightCheck check) =>
        !string.Equals(check.Result, Pass, StringComparison.OrdinalIgnoreCase) && !string.Equals(check.Result, Warn, StringComparison.OrdinalIgnoreCase);

    private static bool IsPass(PreflightCheck check) => string.Equals(check.Result, Pass, StringComparison.OrdinalIgnoreCase);

    private static string CheckLine(PreflightCheck c) => $"{c.Name}: {c.Result}{(c.Message.Length > 0 ? ". " + c.Message : ".")}";

    public static string DeviceTarget(ResolvedDevice device) => $"{device.Name} (ResourceID {device.ResourceId})";

    /// <summary>The lines that name the targets of a run: the description, the count, the left-out names and a truncation.</summary>
    public static IEnumerable<string> TargetLines(ResolvedTargets targets)
    {
        yield return $"Targets: {targets.Description}. {PivotsLabels.Count(targets.Devices.Count, "device")}.";
        if (targets.IsBuiltInCollection) yield return $"The target is a built-in collection ({targets.CollectionId}). The run reaches every device of the collection.";
        if (targets.Truncated)
            yield return targets.MatchedCount >= 0 ? $"{TruncatedNote} {PivotsLabels.Count(targets.MatchedCount, "device")} match." : TruncatedNote;
        if (targets.Unmatched.Count > 0)
            yield return $"Not found, left out: {UnmatchedText(targets.Unmatched)}";
    }

    public static string UnmatchedText(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(MaxUnmatchedNamed)) + (names.Count > MaxUnmatchedNamed ? $", and {PivotsLabels.Number(names.Count - MaxUnmatchedNamed)} more." : ".");

    public static string ParameterText(IReadOnlyDictionary<string, string>? parameters) =>
        parameters is not { Count: > 0 } ? "Parameters: none; the defaults of the script apply."
            : "Parameters: " + string.Join("; ", parameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key} = {p.Value}")) + ".";

    /// <summary>The plan of a prepared change, or null with the reason when it cannot run.</summary>
    public static (PivotsPlan? Plan, string? Problem) BuildPlan(PivotsChange prepared)
    {
        var blocking = prepared.Checks.Where(Blocks).ToList();
        if (blocking.Count > 0)
            return (null, "The plan cannot run: " + string.Join(" ", blocking.Select(c => c.Message.Length > 0 ? c.Message : $"{c.Name}: {c.Result}.")));
        return prepared.Kind is PivotsChangeKind.RunPivot or PivotsChangeKind.RunScript ? RunPlan(prepared) : ScriptPlan(prepared);
    }

    private static List<string> CheckNotes(PivotsChange prepared, Action<List<string>> middle)
    {
        // The notes box shows the first lines without a scroll, so the checks that need attention come first.
        var notes = new List<string>();
        notes.AddRange(prepared.Checks.Where(c => !IsPass(c)).Select(CheckLine));
        middle(notes);
        notes.AddRange(prepared.Impact);
        notes.AddRange(prepared.Checks.Where(IsPass).Select(CheckLine));
        return notes;
    }

    private static (PivotsPlan?, string?) RunPlan(PivotsChange prepared)
    {
        if (prepared.Targets is not { Devices.Count: > 0 } targets) return (null, NoDevices);
        var script = prepared.Kind == PivotsChangeKind.RunScript;
        if (script && (prepared.Script is not { Text: not null } || string.IsNullOrEmpty(prepared.ScriptHash))) return (null, ScriptNotRead);
        var count = targets.Devices.Count;
        var action = script ? "Run script" : "Run query";
        var entries = targets.Devices.Select(d => new PivotsPlanEntry(new ChangePlanItem(action, DeviceTarget(d), $"Online state: {PivotsLabels.Online(d.IsOnline)}."), d.ResourceId)).ToList();
        var notes = CheckNotes(prepared, n =>
        {
            n.AddRange(TargetLines(targets));
            if (script)
            {
                var s = prepared.Script!;
                n.Add($"Script: {s.Name}. Version {s.Version}. GUID {s.Guid}.");
                n.Add($"SHA-256 of the script text: {prepared.ScriptHash}.");
                n.Add(ParameterText(prepared.Parameters));
            }
        });
        notes.Add(script ? NoUndo : PivotNote);
        notes.Add(StopWaitingNote);
        var devices = PivotsLabels.Count(count, "device");
        var plan = new ChangePlan(Title(prepared.Kind), $"Run on {devices}", entries.Select(e => e.Item), isDestructive: script, hasUndo: false)
        {
            DestructiveConfirmation = script ? ScriptRunBox : null,
            ExtraConfirmation = script ? $"Run {prepared.Script!.Name} version {prepared.Script.Version} on {devices}." : null,
            SecondConfirmation = targets.IsBuiltInCollection ? $"This runs on every device of the site: {targets.Description}." : null,
            Notes = notes,
            Body = script ? prepared.Script!.Text : prepared.QueryText ?? "",
            BodyName = script ? "Script text" : "Query text",
            LogName = PivotsLabels.Kind(prepared.Kind)
        };
        return (new PivotsPlan(PivotsLabels.Kind(prepared.Kind), prepared, plan, entries), null);
    }

    private static (PivotsPlan?, string?) ScriptPlan(PivotsChange prepared)
    {
        var kind = prepared.Kind;
        var script = prepared.Script;
        if (kind != PivotsChangeKind.SubmitScript && script is null) return (null, ScriptNotRead);
        var name = kind == PivotsChangeKind.SubmitScript ? prepared.SubmitName ?? "" : script!.Name;
        var guid = kind == PivotsChangeKind.SubmitScript ? null : script!.Guid;
        var target = guid is null ? name : $"{name} ({guid})";
        var (verb, detail, note) = kind switch
        {
            PivotsChangeKind.SubmitScript => ("Submit", "The site stores the script as Waiting for approval.", SubmitNote),
            PivotsChangeKind.ApproveScript => ("Approve", $"State now: {PivotsLabels.ScriptState(script!.State)}.", ApproveNote),
            PivotsChangeKind.DenyScript => ("Deny", $"State now: {PivotsLabels.ScriptState(script!.State)}.", DenyNote),
            _ => ("Remove", $"State now: {PivotsLabels.ScriptState(script!.State)}.", RemoveNote)
        };
        var entries = new List<PivotsPlanEntry> { new(new ChangePlanItem(verb, target, detail), null) };
        var notes = CheckNotes(prepared, n =>
        {
            if (script is not null) n.Add($"Script: {script.Name}. Version {script.Version}. Author {script.Author}.");
            if (prepared.ScriptHash is { Length: > 0 } hash) n.Add($"SHA-256 of the script text: {hash}.");
            if (kind == PivotsChangeKind.DenyScript) n.Add($"Comment: {prepared.Comment}");
        });
        notes.Add(note);
        var remove = kind == PivotsChangeKind.RemoveScript;
        var plan = new ChangePlan(Title(kind), $"{verb} {name}", entries.Select(e => e.Item), isDestructive: remove, hasUndo: false)
        {
            ExtraConfirmation = remove ? $"Remove {name}. Its run history goes with it." : null,
            Notes = notes,
            Body = kind == PivotsChangeKind.SubmitScript ? prepared.SubmitText ?? "" : script?.Text,
            BodyName = "Script text",
            LogName = PivotsLabels.Kind(kind)
        };
        return (new PivotsPlan(PivotsLabels.Kind(kind), prepared, plan, entries), null);
    }

    /// <summary>
    /// A PowerShell expression that gives back the rows as objects. The rows travel as Base64 of their
    /// JSON, so no cell text can end the string or add a command.
    /// </summary>
    public static string CopyAsPowerShell(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string>> rows)
    {
        var list = rows.Select(r =>
        {
            var o = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
            {
                var name = columns[i];
                while (o.ContainsKey(name)) name += "_";
                o[name] = i < r.Count ? r[i] : "";
            }
            return o;
        }).ToList();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list)));
        return $"[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encoded}')) | ConvertFrom-Json";
    }
}
