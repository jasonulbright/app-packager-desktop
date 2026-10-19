using System.Globalization;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads and changes maintenance windows through the ConfigMgr session. Each change is one
/// script run that checks the current state (the collection exists, the name is free, the
/// window is unchanged since it was captured) before it writes, and reads the window back
/// after it. A write runs to its end once started: cancellation takes effect between
/// changes. A rename creates the new window before it removes the old one.
/// </summary>
public sealed class MaintenanceWindowService : IMaintenanceWindowService
{
    public const string LogArea = "MaintenanceWindows";

    internal const string ListResource = "ApplicationPackagerEngine.Services.MaintenanceWindows.Scripts.ListMaintenanceWindows.ps1";
    internal const string WriteResource = "ApplicationPackagerEngine.Services.MaintenanceWindows.Scripts.WriteMaintenanceWindow.ps1";

    internal const string AccessHint =
        "The account needs a ConfigMgr security role with the Modify Collection Setting permission on the collection.";

    private static readonly ConfigMgrScript ListScript = ConfigMgrScript.FromResource(ListResource);
    private static readonly ConfigMgrScript WriteScript = ConfigMgrScript.FromResource(WriteResource);

    private readonly IConfigMgrSession _session;
    private readonly IScheduleProjector _projector;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _now;

    public MaintenanceWindowService(IConfigMgrSession session, IScheduleProjector? projector = null, IAppLog? log = null, Func<DateTime>? now = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _projector = projector ?? new ScheduleProjector();
        _log = log;
        _now = now ?? (() => DateTime.Now);
    }

    internal sealed record Row(
        string? Kind,
        string? CollectionId,
        string? Name,
        string? FolderPath,
        int MemberCount,
        int WindowCount,
        string? ServiceWindowId,
        int Type,
        bool Enabled,
        string? Schedules,
        string? Description);

    internal sealed record WriteResult(string? Kind, string? Status, string? Detail, Row? Window);

    public static bool IsBuiltIn(string? collectionId) =>
        collectionId is not null && collectionId.StartsWith("SMS", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct)
    {
        var rows = await ReadAsync(new ConfigMgrParameters { { "CollectionsOnly", true } }, ct).ConfigureAwait(false);
        var collections = rows.Where(r => r.Kind == "Collection").Select(ToCollection).ToList();
        _log?.Info(LogArea, $"Listed {collections.Count} device collections.");
        return collections;
    }

    public async Task<IReadOnlyList<MaintenanceWindowInfo>> ListWindowsAsync(CancellationToken ct)
    {
        var rows = await ReadAsync(new ConfigMgrParameters(), ct).ConfigureAwait(false);
        var names = rows.Where(r => r.Kind == "Collection")
            .GroupBy(r => r.CollectionId ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name ?? "", StringComparer.OrdinalIgnoreCase);
        var windows = rows.Where(r => r.Kind == "Window")
            .Select(r => ToInfo(r, names.GetValueOrDefault(r.CollectionId ?? "", "")))
            .ToList();
        _log?.Info(LogArea, $"Listed {windows.Count} maintenance windows on {names.Count} device collections.");
        return windows;
    }

    public async Task<MaintenanceChange> PrepareAsync(MaintenanceChange request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CollectionId))
            throw new MaintenanceWindowException("Select a collection.");
        if (IsBuiltIn(request.CollectionId))
            throw new MaintenanceWindowException(BuiltInMessage(request.CollectionName, request.CollectionId));

        var rows = await ReadAsync(new ConfigMgrParameters { { "CollectionId", request.CollectionId } }, ct).ConfigureAwait(false);
        var collection = rows.FirstOrDefault(r => r.Kind == "Collection"
            && string.Equals(r.CollectionId, request.CollectionId, StringComparison.OrdinalIgnoreCase));
        if (collection is null)
            throw new MaintenanceWindowException(CollectionMissingMessage(request.CollectionName, request.CollectionId));
        var collectionName = collection.Name ?? request.CollectionName;
        var windows = rows.Where(r => r.Kind == "Window").Select(r => ToInfo(r, collectionName)).ToList();
        var change = request with { CollectionId = collection.CollectionId ?? request.CollectionId, CollectionName = collectionName };

        MaintenanceWindowInfo? Named(string? name) =>
            windows.FirstOrDefault(w => string.Equals(w.WindowName, name, StringComparison.OrdinalIgnoreCase));

        switch (request.Kind)
        {
            case ChangeKind.Create:
            case ChangeKind.ApplyTemplate:
            {
                var name = request.Kind == ChangeKind.ApplyTemplate ? request.NewWindowName ?? request.WindowName : CreateName(request);
                CheckNewWindow(request.After, name);
                var taken = Named(name);
                if (taken is not null)
                {
                    if (request.Kind == ChangeKind.Create || request.NewWindowName is not null)
                        throw new MaintenanceWindowException(NameTakenMessage(collectionName, name));
                    return change with { Before = taken };
                }
                return change with { Before = null, After = request.After! with { CollectionId = change.CollectionId, CollectionName = collectionName, WindowName = name } };
            }
            case ChangeKind.Update:
            case ChangeKind.Rename:
            case ChangeKind.SetEnabled:
            case ChangeKind.Remove:
            {
                var current = request.Before is { ServiceWindowId.Length: > 0 } before
                    ? windows.FirstOrDefault(w => string.Equals(w.ServiceWindowId, before.ServiceWindowId, StringComparison.OrdinalIgnoreCase))
                    : Named(request.WindowName);
                if (current is null)
                    throw new MaintenanceWindowException(WindowMissingMessage(collectionName, request.Before?.WindowName ?? request.WindowName));
                if (request.Kind != ChangeKind.SetEnabled && !IsReadable(current))
                    throw new MaintenanceWindowException(
                        $"The schedule of window {current.WindowName} uses a form this page cannot show, so it is not changed or removed here. Use the ConfigMgr console.");
                change = change with { WindowName = current.WindowName, Before = current };
                switch (request.Kind)
                {
                    case ChangeKind.Update:
                        CheckNewWindow(request.After, current.WindowName);
                        return change with { After = request.After! with { CollectionId = change.CollectionId, CollectionName = collectionName, WindowName = current.WindowName, ServiceWindowId = current.ServiceWindowId } };
                    case ChangeKind.Rename:
                        var newName = request.NewWindowName?.Trim() ?? "";
                        var target = (request.After ?? current) with { CollectionId = change.CollectionId, CollectionName = collectionName, WindowName = newName };
                        CheckNewWindow(target, newName);
                        if (Named(newName) is not null)
                            throw new MaintenanceWindowException(NameTakenMessage(collectionName, newName));
                        return change with { NewWindowName = newName, After = target };
                    case ChangeKind.SetEnabled:
                        var enabled = request.After?.Enabled ?? !current.Enabled;
                        return change with { After = current with { Enabled = enabled } };
                    default:
                        return change with { After = null };
                }
            }
            default:
                throw new MaintenanceWindowException("The change kind is not known.");
        }
    }

    public async Task<IReadOnlyList<MaintenanceOutcome>> ApplyAsync(
        IReadOnlyList<MaintenanceChange> changes, IProgress<MaintenanceOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(progress);
        var outcomes = new List<MaintenanceOutcome>(changes.Count);
        var skipped = 0;
        foreach (var change in changes)
        {
            MaintenanceOutcome outcome;
            if (ct.IsCancellationRequested)
            {
                outcome = new MaintenanceOutcome(change, false, "Skipped: the run was cancelled before this change.", null);
                skipped++;
            }
            else
            {
                outcome = await ApplyOneAsync(change).ConfigureAwait(false);
                _log?.Info(LogArea, $"{change.Kind}: {(outcome.Succeeded ? "succeeded" : "not applied")}.");
            }
            outcomes.Add(outcome);
            progress.Report(outcome);
        }
        var succeeded = outcomes.Count(o => o.Succeeded);
        _log?.Info(LogArea, $"Applied {changes.Count} changes: {succeeded} succeeded, {outcomes.Count - succeeded - skipped} failed, {skipped} skipped.");
        return outcomes;
    }

    private async Task<MaintenanceOutcome> ApplyOneAsync(MaintenanceChange change)
    {
        var collection = string.IsNullOrWhiteSpace(change.CollectionName) ? change.CollectionId : change.CollectionName;
        if (IsBuiltIn(change.CollectionId))
            return Fail(change, BuiltInMessage(change.CollectionName, change.CollectionId));

        switch (change.Kind)
        {
            case ChangeKind.Create:
            case ChangeKind.ApplyTemplate:
            {
                var name = change.Kind == ChangeKind.ApplyTemplate ? change.NewWindowName ?? change.WindowName : CreateName(change);
                if (change.Kind == ChangeKind.ApplyTemplate && change.Before is not null && change.NewWindowName is null)
                    return Fail(change, $"Skipped: collection {collection} already has a window named {name}.");
                if (InvalidNewWindow(change.After, name) is { } invalid) return Fail(change, invalid);
                var (result, failure) = await CreateAsync(change, change.After!, name).ConfigureAwait(false);
                if (failure is not null) return failure;
                var created = ToInfo(result!.Window!, change.CollectionName);
                return Done(change, $"Created window {name} on {collection}." + StoredNote(change.After!.Schedule, created),
                    new MaintenanceChange(ChangeKind.Remove, change.CollectionId, change.CollectionName, created.WindowName, null, created, null));
            }
            case ChangeKind.Update:
            {
                if (change.Before is null) return Fail(change, NotPreparedMessage);
                if (InvalidNewWindow(change.After, change.Before.WindowName) is { } invalid) return Fail(change, invalid);
                var parameters = Write("Update", change, change.Before);
                AddWindow(parameters, change.After!);
                var (result, failure) = await RunWriteAsync(change, parameters, change.Before.WindowName).ConfigureAwait(false);
                if (failure is not null) return failure;
                var updated = ToInfo(result!.Window!, change.CollectionName);
                return Done(change, $"Updated window {updated.WindowName} on {collection}." + StoredNote(change.After!.Schedule, updated),
                    new MaintenanceChange(ChangeKind.Update, change.CollectionId, change.CollectionName, updated.WindowName, null, updated, change.Before));
            }
            case ChangeKind.Rename:
            {
                if (change.Before is null) return Fail(change, NotPreparedMessage);
                var newName = change.NewWindowName?.Trim() ?? "";
                var target = change.After ?? change.Before;
                if (InvalidNewWindow(target, newName) is { } invalid) return Fail(change, invalid);
                var (createdResult, createFailure) = await CreateAsync(change, target, newName).ConfigureAwait(false);
                if (createFailure is not null)
                    return createFailure with { Message = createFailure.Message + $" The window {change.Before.WindowName} is not changed." };
                var created = ToInfo(createdResult!.Window!, change.CollectionName);
                var undoCreate = new MaintenanceChange(ChangeKind.Remove, change.CollectionId, change.CollectionName, created.WindowName, null, created, null);

                var remove = Write("Remove", change, change.Before);
                var (_, removeFailure) = await RunWriteAsync(change, remove, change.Before.WindowName).ConfigureAwait(false);
                if (removeFailure is not null)
                    return new MaintenanceOutcome(change, false,
                        $"Created window {newName} on {collection}, but the old window {change.Before.WindowName} was not removed: {removeFailure.Message} "
                        + $"Both windows exist now. Remove {change.Before.WindowName} to finish the rename.",
                        undoCreate);
                return Done(change, $"Renamed window {change.Before.WindowName} to {newName} on {collection}. The window has a new ID." + StoredNote(target.Schedule, created),
                    new MaintenanceChange(ChangeKind.Rename, change.CollectionId, change.CollectionName, created.WindowName, change.Before.WindowName, created, change.Before));
            }
            case ChangeKind.SetEnabled:
            {
                if (change.Before is null) return Fail(change, NotPreparedMessage);
                var enabled = change.After?.Enabled ?? !change.Before.Enabled;
                var parameters = Write("SetEnabled", change, change.Before);
                parameters["IsEnabled"] = enabled;
                var (result, failure) = await RunWriteAsync(change, parameters, change.Before.WindowName).ConfigureAwait(false);
                if (failure is not null) return failure;
                var after = ToInfo(result!.Window!, change.CollectionName);
                return Done(change, $"{(enabled ? "Enabled" : "Disabled")} window {after.WindowName} on {collection}.",
                    new MaintenanceChange(ChangeKind.SetEnabled, change.CollectionId, change.CollectionName, after.WindowName, null, after,
                        after with { Enabled = change.Before.Enabled }));
            }
            case ChangeKind.Remove:
            {
                if (change.Before is null) return Fail(change, NotPreparedMessage);
                if (!IsReadable(change.Before))
                    return Fail(change, $"The schedule of window {change.Before.WindowName} uses a form this page cannot show, so it is not removed here. Use the ConfigMgr console.");
                var (_, failure) = await RunWriteAsync(change, Write("Remove", change, change.Before), change.Before.WindowName).ConfigureAwait(false);
                if (failure is not null) return failure;
                return Done(change, $"Removed window {change.Before.WindowName} from {collection}. Undo creates it again as a new window with a new ID.",
                    new MaintenanceChange(ChangeKind.Create, change.CollectionId, change.CollectionName, change.Before.WindowName, null, null, change.Before));
            }
            default:
                return Fail(change, "The change kind is not known.");
        }
    }

    private const string NotPreparedMessage = "The change has no captured window state, so it was not applied. Refresh the list and try again.";

    private static string CreateName(MaintenanceChange change) =>
        string.IsNullOrWhiteSpace(change.WindowName) ? change.After?.WindowName?.Trim() ?? "" : change.WindowName.Trim();

    private Task<(WriteResult? Result, MaintenanceOutcome? Failure)> CreateAsync(MaintenanceChange change, MaintenanceWindowInfo window, string name)
    {
        var parameters = new ConfigMgrParameters
        {
            { "Operation", "Create" },
            { "CollectionId", change.CollectionId },
            { "Name", name }
        };
        AddWindow(parameters, window);
        return RunWriteAsync(change, parameters, name);
    }

    private static ConfigMgrParameters Write(string operation, MaintenanceChange change, MaintenanceWindowInfo before) => new()
    {
        { "Operation", operation },
        { "CollectionId", change.CollectionId },
        { "ServiceWindowId", before.ServiceWindowId },
        { "ExpectedName", before.WindowName },
        { "ExpectedSchedules", IsReadable(before) ? ScheduleToken.Equivalents(Normalized(before.Schedule)).ToArray() : Array.Empty<string>() },
        { "ExpectedType", ServiceWindowType(before.WindowType) },
        { "ExpectedEnabled", before.Enabled }
    };

    private static void AddWindow(ConfigMgrParameters parameters, MaintenanceWindowInfo window)
    {
        var s = Normalized(window.Schedule);
        parameters["ApplyTo"] = MaintenanceWindowTypes.ToApplyTo(window.WindowType);
        parameters["IsEnabled"] = window.Enabled;
        parameters["Start"] = s.Start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        var minutes = (int)Math.Round(s.Duration.TotalMinutes);
        parameters["DurationHours"] = minutes / 60;
        parameters["DurationMinutes"] = minutes % 60;
        parameters["IsUtc"] = s.IsUtc;
        parameters["RecurCount"] = Math.Max(1, s.Interval);
        switch (s.Kind)
        {
            case MaintenanceScheduleKind.OneTime:
                parameters["Recurrence"] = "None";
                break;
            case MaintenanceScheduleKind.Daily:
                parameters["Recurrence"] = "Days";
                break;
            case MaintenanceScheduleKind.Weekly:
                parameters["Recurrence"] = "Weekly";
                parameters["DayOfWeek"] = s.DayOfWeek!.Value.ToString();
                break;
            case MaintenanceScheduleKind.MonthlyByDate:
                parameters["Recurrence"] = "MonthlyByDate";
                parameters["DayOfMonth"] = s.DayOfMonth!.Value;
                break;
            case MaintenanceScheduleKind.MonthlyByWeekday:
                parameters["Recurrence"] = "MonthlyByWeekday";
                parameters["DayOfWeek"] = s.DayOfWeek!.Value.ToString();
                parameters["WeekOrder"] = WeekOrderName(s.WeekOrder!.Value);
                parameters["OffsetDay"] = s.PatchTuesdayOffsetDays;
                break;
            case MaintenanceScheduleKind.PatchTuesday:
                var (week, offset) = ScheduleToken.PatchTuesdayForm(s.PatchTuesdayOffsetDays);
                parameters["Recurrence"] = "MonthlyByWeekday";
                parameters["DayOfWeek"] = nameof(DayOfWeek.Tuesday);
                parameters["WeekOrder"] = WeekOrderName(week);
                parameters["OffsetDay"] = offset;
                break;
        }
    }

    private static string WeekOrderName(int week) => week switch
    {
        1 => "First",
        2 => "Second",
        3 => "Third",
        4 => "Fourth",
        _ => "Last"
    };

    private static MaintenanceSchedule Normalized(MaintenanceSchedule s) => s with
    {
        Start = new DateTime(s.Start.Year, s.Start.Month, s.Start.Day, s.Start.Hour, s.Start.Minute, 0, DateTimeKind.Unspecified),
        Interval = s.Kind == MaintenanceScheduleKind.OneTime ? 1 : s.Interval,
        DayOfWeek = s.Kind is MaintenanceScheduleKind.Weekly or MaintenanceScheduleKind.MonthlyByWeekday ? s.DayOfWeek : null,
        DayOfMonth = s.Kind == MaintenanceScheduleKind.MonthlyByDate ? s.DayOfMonth : null,
        WeekOrder = s.Kind == MaintenanceScheduleKind.MonthlyByWeekday ? s.WeekOrder : null,
        PatchTuesdayOffsetDays = s.Kind is MaintenanceScheduleKind.MonthlyByWeekday or MaintenanceScheduleKind.PatchTuesday ? s.PatchTuesdayOffsetDays : 0
    };

    private string StoredNote(MaintenanceSchedule requested, MaintenanceWindowInfo stored)
    {
        if (!IsReadable(stored)) return " ConfigMgr stored a schedule this page cannot show.";
        var wanted = ScheduleToken.Decode(ScheduleToken.Encode(Normalized(requested)));
        return wanted == stored.Schedule ? "" : $" ConfigMgr stored a different schedule: {stored.ScheduleText}, {MaintenanceReportWriter.DurationText(stored.Schedule.Duration)} long.";
    }

    private static int ServiceWindowType(string type)
    {
        if (MaintenanceWindowTypes.IsKnown(type)) return MaintenanceWindowTypes.ToServiceWindowType(type);
        var digits = new string(type.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private string? InvalidNewWindow(MaintenanceWindowInfo? window, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a window name.";
        if (window is null) return "The change has no window values.";
        if (!MaintenanceWindowTypes.IsKnown(window.WindowType)) return "Select the window type: any, software updates or task sequences.";
        if (window.Schedule is null) return "The window has no schedule.";
        return _projector.Validate(window.Schedule);
    }

    private void CheckNewWindow(MaintenanceWindowInfo? window, string name)
    {
        if (InvalidNewWindow(window, name) is { } reason) throw new MaintenanceWindowException(reason);
    }

    private static bool IsReadable(MaintenanceWindowInfo window) => window.Schedule is { } s && s.Duration > TimeSpan.Zero;

    private async Task<(WriteResult? Result, MaintenanceOutcome? Failure)> RunWriteAsync(MaintenanceChange change, ConfigMgrParameters parameters, string windowName)
    {
        var collection = string.IsNullOrWhiteSpace(change.CollectionName) ? change.CollectionId : change.CollectionName;
        WriteResult? result;
        try
        {
            var output = await _session.InvokeAsync(WriteScript, parameters, CancellationToken.None).ConfigureAwait(false);
            result = output.As<WriteResult>().FirstOrDefault(r => r.Kind == "Result");
        }
        catch (ConfigMgrSessionException ex)
        {
            return (null, Fail(change, WriteFailureMessage(ex, collection, windowName)));
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log?.Warning(LogArea, $"{change.Kind} returned output that could not be read ({ex.GetType().Name}).");
            result = null;
        }

        switch (result?.Status)
        {
            case "ok" when result.Window is not null:
                return (result, null);
            case "collection-missing":
                return (result, Fail(change, CollectionMissingMessage(change.CollectionName, change.CollectionId)));
            case "name-taken":
                return (result, Fail(change, change.Kind == ChangeKind.ApplyTemplate
                    ? $"Skipped: collection {collection} already has a window named {windowName}."
                    : NameTakenMessage(collection, windowName)));
            case "window-missing":
                return (result, Fail(change, WindowMissingMessage(collection, windowName)));
            case "changed":
                return (result, Fail(change, $"The window {windowName} on {collection} changed after the plan was shown, so it was not changed. Refresh the list and try again."));
            case "schedule-rejected":
                LogDetail(change, result.Detail);
                return (result, Fail(change, $"ConfigMgr did not accept the schedule of window {windowName}. The log has the details."));
            default:
                LogDetail(change, result?.Detail ?? "The script returned no result.");
                return (result, Fail(change, $"ConfigMgr did not make the change to window {windowName} on {collection}. The log has the details."));
        }
    }

    private void LogDetail(MaintenanceChange change, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
            _log?.Warning(LogArea, $"{change.Kind} was not applied: {SecretMask.Apply(detail, [])}");
    }

    private string WriteFailureMessage(ConfigMgrSessionException ex, string collection, string windowName)
    {
        switch (ex.Failure)
        {
            case ConfigMgrFailure.AccessDenied:
                return $"ConfigMgr denied the change to window {windowName} on {collection}. {AccessHint}";
            case ConfigMgrFailure.Timeout:
            case ConfigMgrFailure.ProcessDied:
            case ConfigMgrFailure.Unexpected:
                return $"The result of the change to window {windowName} on {collection} is not known: {ex.Message} Refresh the list to see the current state.";
            case ConfigMgrFailure.ScriptFailed:
                _log?.Warning(LogArea, $"A change failed in the ConfigMgr session: {ex.Message}");
                return $"ConfigMgr reported an error for window {windowName} on {collection}. The log has the details.";
            default:
                return ex.Message;
        }
    }

    private async Task<IReadOnlyList<Row>> ReadAsync(ConfigMgrParameters parameters, CancellationToken ct)
    {
        try
        {
            var result = await _session.InvokeAsync(ListScript, parameters, ct).ConfigureAwait(false);
            return result.As<Row>();
        }
        catch (ConfigMgrSessionException ex)
        {
            throw new MaintenanceWindowException(ex.Failure switch
            {
                ConfigMgrFailure.AccessDenied => $"{ex.Message} {AccessHint}",
                ConfigMgrFailure.ScriptFailed => LoggedReadFailure(ex),
                _ => ex.Message
            });
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log?.Warning(LogArea, $"The maintenance window list could not be read ({ex.GetType().Name}).");
            throw new MaintenanceWindowException("The maintenance windows could not be read from ConfigMgr. The log has the details.");
        }
    }

    private string LoggedReadFailure(ConfigMgrSessionException ex)
    {
        _log?.Warning(LogArea, $"Reading maintenance windows failed in the ConfigMgr session: {ex.Message}");
        return "The maintenance windows could not be read from ConfigMgr. The log has the details.";
    }

    private static CollectionInfo ToCollection(Row row)
    {
        var id = row.CollectionId ?? "";
        return new CollectionInfo(id, row.Name ?? "", FolderPath(row.FolderPath), row.MemberCount, IsBuiltIn(id));
    }

    /// <summary>ObjectPath "/Servers/Production" as "Servers\Production"; the root is empty.</summary>
    internal static string FolderPath(string? objectPath) =>
        string.Join('\\', (objectPath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private MaintenanceWindowInfo ToInfo(Row row, string collectionName)
    {
        var schedule = ScheduleToken.Decode(row.Schedules);
        var type = MaintenanceWindowTypes.FromServiceWindowType(row.Type);
        if (schedule is null)
        {
            var unreadable = new MaintenanceSchedule(MaintenanceScheduleKind.OneTime, DateTime.MinValue, TimeSpan.Zero, false, 1, null, null, null, 0, null);
            return new MaintenanceWindowInfo(row.CollectionId ?? "", collectionName, row.Name ?? "", row.ServiceWindowId ?? "", type, row.Enabled,
                unreadable, row.Description ?? "", null, row.Description ?? "");
        }
        DateTime? next = _projector.NextOccurrences(schedule, _now(), 1) is [var first, ..] ? first : null;
        return new MaintenanceWindowInfo(row.CollectionId ?? "", collectionName, row.Name ?? "", row.ServiceWindowId ?? "", type, row.Enabled,
            schedule, _projector.Describe(schedule), next, row.Description ?? "");
    }

    private static MaintenanceOutcome Fail(MaintenanceChange change, string message) => new(change, false, message, null);

    private static MaintenanceOutcome Done(MaintenanceChange change, string message, MaintenanceChange undo) => new(change, true, message, undo);

    private static string BuiltInMessage(string? name, string id) =>
        $"{Shown(name, id)} is a built-in collection. Maintenance windows on built-in collections are not changed here.";

    private static string CollectionMissingMessage(string? name, string id) =>
        $"The collection {Shown(name, id)} does not exist any more. Refresh the list.";

    private static string NameTakenMessage(string collection, string name) =>
        $"Collection {collection} already has a window named {name}.";

    private static string WindowMissingMessage(string collection, string name) =>
        $"The window {name} does not exist on {collection} any more. Refresh the list.";

    private static string Shown(string? name, string id) => string.IsNullOrWhiteSpace(name) ? id : $"{name} ({id})";
}
