using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The rewrite of the version clause of the old applications of a line as a change plan: the list of clauses, the confirmation text of the
/// specification, and Undo as Restore. The window is the change plan window; this class builds what it shows and what it runs. The log
/// <c>supersedence-rewrite.jsonl</c> is written ahead, one entry before each change, exactly as the command line does.
/// </summary>
public static class RewriteOldVersionsPlans
{
    public const string Title = "Rewrite the version clause of the old versions";
    public const string RestoreTitle = "Restore the version clause of the old versions";

    public static string LogPath => Path.Combine(AppDataPaths.LegacyRoot, "supersedence-rewrite.jsonl");

    public static ChangePlanItem ItemOf(RewriteItem item) =>
        new("Rewrite clause", $"{item.Application} / {item.DeploymentType}", "GreaterEquals -> IsEquals");

    public static ChangePlanItem RestoreItemOf(RewriteLogEntry entry) =>
        new("Restore clause", $"{entry.Application} / {entry.DeploymentType}", $"{entry.NewOperator} -> {entry.OldOperator}");

    /// <summary>The lines above the list: what is blocked, what is left and why, and the effect on the devices.</summary>
    public static IReadOnlyList<string> NotesOf(RewritePlan plan, int restorable)
    {
        var notes = new List<string> { RewritePlanner.ConfirmationText };
        notes.AddRange(plan.Blocked.Select(b => "Blocked: " + b));
        notes.AddRange(plan.Items.Where(i => !i.Rewrites).Select(i => $"Left: {i.Application} / {i.DeploymentType}: {i.Skip}"));
        if (restorable > 0)
            notes.Add($"{restorable} earlier rewrite(s) can still be restored. Open this window again when nothing is left to rewrite.");
        return notes;
    }

    /// <summary>The plan, the run and the undo of a rewrite, or null when the plan has no clause to rewrite.</summary>
    public static (ChangePlan Plan, ChangePlanRun Run, ChangePlanRun Undo)? ForRewrite(RewritePlan plan, RewriteScope scope, int restorable)
    {
        var rewrites = plan.Items.Where(i => i.Rewrites).ToList();
        if (rewrites.Count == 0) return null;
        var items = rewrites.Select(ItemOf).ToList();
        var change = new ChangePlan(Title, $"Rewrite {rewrites.Count} clause(s)", items, isDestructive: true, hasUndo: true)
        {
            DestructiveConfirmation = RewritePlanner.ConfirmationText,
            Notes = NotesOf(plan, restorable),
            LogName = "Rewrite old version clauses"
        };
        var keys = rewrites.Select(r => (r.Application, r.DeploymentType)).ToList();
        ChangePlanRun run = async (confirmed, progress, ct) =>
        {
            // Each clause is reported as soon as it is written, so a cancel keeps the rows and Undo. The clause whose intent is in the log and
            // whose end is not known when the run stops is reported as changed in part: Restore writes the old operator in either case.
            (string Application, string DeploymentType)? inFlight = null;
            void Persist(RewriteLogEntry entry)
            {
                Append(entry);
                inFlight = entry.Restored ? null : (entry.Application, entry.DeploymentType);
            }
            void Written(string line)
            {
                if (inFlight is not { } key) return;
                progress.Report(new ChangePlanItemResult(items[IndexOf(keys, key)], true, "Written to the site and the log."));
                inFlight = null;
            }
            try
            {
                var outcome = await new OldVersionRewriteRunner().WriteRewriteAsync(plan, scope, Persist, Written, ct);
                return Result(items, keys, outcome);
            }
            catch (OperationCanceledException)
            {
                if (inFlight is { } key)
                    progress.Report(new ChangePlanItemResult(items[IndexOf(keys, key)], false,
                        "The run was cancelled while this clause was written. It may be changed; Undo restores it.") { PartialChange = true });
                throw;
            }
        };
        return (change, run, RestoreRun(scope));
    }

    /// <summary>The plan of a restore of the rewrites that the log lists and no restore entry cancels, or null when none is pending.</summary>
    public static (ChangePlan Plan, ChangePlanRun Run)? ForRestore(RewriteScope scope)
    {
        var pending = Pending();
        if (pending.Count == 0) return null;
        var change = new ChangePlan(RestoreTitle, $"Restore {pending.Count} clause(s)", pending.Select(RestoreItemOf), isDestructive: true)
        {
            DestructiveConfirmation = "The old operator is written back on the site. A device that now reads \"installed\" through the clause may read it differently.",
            LogName = "Restore old version clauses"
        };
        return (change, RestoreRun(scope));
    }

    public static IReadOnlyList<RewriteLogEntry> Pending()
    {
        var log = ReadLog();
        return RewriteLog.ToRestore(log, log.Select(e => e.Application).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static IReadOnlyList<RewriteLogEntry> ReadLog() => File.Exists(LogPath) ? RewriteLog.Read(File.ReadAllText(LogPath)) : [];

    private static ChangePlanRun RestoreRun(RewriteScope scope) => async (confirmed, progress, ct) =>
    {
        var pending = Pending();
        var items = pending.Select(RestoreItemOf).ToList();
        var keys = pending.Select(p => (p.Application, p.DeploymentType)).ToList();
        // A restore entry is written after each restored clause, so each one is reported as it is persisted.
        void Persist(RewriteLogEntry entry)
        {
            Append(entry);
            if (IndexOf(keys, (entry.Application, entry.DeploymentType)) is var at and >= 0)
                progress.Report(new ChangePlanItemResult(items[at], true, "Written to the site and the log."));
        }
        var outcome = await new OldVersionRewriteRunner().RestoreAsync(ReadLog(), scope, Persist, null, ct);
        return Result(items, keys, outcome);
    };

    /// <summary>Maps the outcome of the runner to one result row per listed clause; a failure text that names the clause is its message.</summary>
    public static ChangePlanResult Result(IReadOnlyList<ChangePlanItem> items, IReadOnlyList<(string Application, string DeploymentType)> keys, RewriteOutcome outcome)
    {
        var rows = new List<ChangePlanItemResult>();
        for (var i = 0; i < items.Count; i++)
        {
            var (application, type) = keys[i];
            var done = outcome.Written.Any(w => Same(w.Application, application) && Same(w.DeploymentType, type));
            var failure = outcome.Failures.FirstOrDefault(f => f.StartsWith($"{application} / {type}:", StringComparison.OrdinalIgnoreCase));
            rows.Add(done ? new ChangePlanItemResult(items[i], true, "Written to the site and the log.")
                : failure is not null ? new ChangePlanItemResult(items[i], false, failure[(application.Length + type.Length + 4)..].Trim())
                : ChangePlanItemResult.Skip(items[i], "Not reached: the run stopped before this clause."));
        }
        return new ChangePlanResult(rows) { UndoAvailable = rows.Any(r => r.Succeeded) };
    }

    private static int IndexOf(IReadOnlyList<(string Application, string DeploymentType)> keys, (string Application, string DeploymentType) key)
    {
        for (var i = 0; i < keys.Count; i++)
            if (Same(keys[i].Application, key.Application) && Same(keys[i].DeploymentType, key.DeploymentType)) return i;
        return -1;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Appends one entry and closes the file, so the entry is on disk before the run goes on. A failure stops the rewrite.</summary>
    private static void Append(RewriteLogEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(RewriteLog.Serialize(entry));
    }
}
