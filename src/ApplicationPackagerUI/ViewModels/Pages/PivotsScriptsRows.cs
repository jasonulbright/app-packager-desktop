using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The labels the Pivots and Scripts page shows for states, kinds and counts.</summary>
public static class PivotsLabels
{
    public const string Unknown = "Not known";

    public static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Count(int count, string singular, string? plural = null) =>
        $"{Number(count)} {(count == 1 ? singular : plural ?? singular + "s")}";

    public static string DeviceState(string state) => state switch
    {
        PivotsDeviceStates.Sent => "Sent",
        PivotsDeviceStates.Running => "Running",
        PivotsDeviceStates.Completed => "Completed",
        PivotsDeviceStates.Failed => "Failed",
        PivotsDeviceStates.TimedOut => "Timed out",
        PivotsDeviceStates.NoResponse => "No response",
        PivotsDeviceStates.Skipped => "Skipped",
        { Length: > 0 } other => other,
        _ => Unknown
    };

    /// <summary>True for a state that ends the wait for a device: it answered, failed or cannot answer any more.</summary>
    public static bool IsFinal(string state) => state is PivotsDeviceStates.Completed or PivotsDeviceStates.Failed or PivotsDeviceStates.TimedOut
        or PivotsDeviceStates.NoResponse or PivotsDeviceStates.Skipped;

    public static string Online(bool? online) => online switch { true => "Online", false => "Offline", null => Unknown };

    public static string ScriptState(string state) => state switch
    {
        SiteScriptStates.Waiting => "Waiting for approval",
        SiteScriptStates.Approved => "Approved",
        SiteScriptStates.Declined => "Declined",
        { Length: > 0 } other => other,
        _ => Unknown
    };

    public static string Kind(PivotsChangeKind kind) => kind switch
    {
        PivotsChangeKind.RunPivot => "Run CMPivot",
        PivotsChangeKind.RunScript => "Run script",
        PivotsChangeKind.SubmitScript => "Submit script",
        PivotsChangeKind.ApproveScript => "Approve script",
        PivotsChangeKind.DenyScript => "Deny script",
        PivotsChangeKind.RemoveScript => "Remove script",
        _ => kind.ToString()
    };

    public static string Kind(string kind) => Enum.TryParse<PivotsChangeKind>(kind, ignoreCase: false, out var parsed) ? Kind(parsed) : kind;

    public static string TargetKind(RunTargetKind kind) => kind switch
    {
        RunTargetKind.Collection => "Collection",
        RunTargetKind.Device => "Device",
        RunTargetKind.Pattern => "Wildcard pattern",
        RunTargetKind.List => "List of names",
        _ => kind.ToString()
    };

    public static string Time(DateTime when) => (when.Kind == DateTimeKind.Utc ? when.ToLocalTime() : when).ToString("g", CultureInfo.CurrentCulture);

    /// <summary>The first line of a text, for a grid cell; the details and the tooltip hold the rest.</summary>
    public static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var trimmed = text.TrimStart('\r', '\n');
        var end = trimmed.IndexOfAny(['\r', '\n']);
        return end < 0 ? trimmed : trimmed[..end] + " …";
    }
}

/// <summary>A target kind with its label, for the kind list.</summary>
public sealed record TargetKindChoice(RunTargetKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the Devices grid: the state of one device in a run.</summary>
public sealed class DeviceGridRow(DeviceResult result, bool? online)
{
    public DeviceResult Result { get; } = result;
    public string Key { get; } = result.ResourceId.ToString(CultureInfo.InvariantCulture);
    public string Device => Result.Name;
    public string ResourceId => Result.ResourceId.ToString(CultureInfo.InvariantCulture);
    public string State => PivotsLabels.DeviceState(Result.State);
    public string Online { get; } = PivotsLabels.Online(online);
    public string Output => PivotsLabels.FirstLine(Result.Output);
    public string ExitCode => Result.ExitCode is { } code ? code.ToString(CultureInfo.InvariantCulture) : "";
    public string Message => Result.Message;
    public override string ToString() => $"{Device}, {State}";
}

/// <summary>One result row: the device and the values in the order of the result columns.</summary>
public sealed record ResultRowData(int ResourceId, string Device, IReadOnlyList<string> Values);

/// <summary>
/// One CMPivot result row; the cells follow the result columns. <see cref="Cells"/> holds the first
/// line of each value for the grid; <see cref="FullCells"/> holds the whole values for the tooltip,
/// the filter and the export.
/// </summary>
public sealed class ResultGridRow(ResultRowData row, int index)
{
    public ResultRowData Row { get; } = row;
    public string Key { get; } = row.ResourceId.ToString(CultureInfo.InvariantCulture) + "#" + index.ToString(CultureInfo.InvariantCulture);
    public string Device => Row.Device;
    public IReadOnlyList<string> Cells { get; } = row.Values.Select(PivotsLabels.FirstLine).ToList();
    public IReadOnlyList<string> FullCells => Row.Values;
    public override string ToString() => Device;
}

/// <summary>
/// One row of the Site scripts grid and of the approved scripts of the Script tab. The list read of
/// the site holds no script text and no parameter definitions; <see cref="Full"/> holds them after the
/// read of the one script.
/// </summary>
public sealed partial class ScriptGridRow(SiteScript script) : ObservableObject
{
    public const string NotRead = "Not read";

    public SiteScript Script { get; } = script;
    public string Key { get; } = script.Guid.ToUpperInvariant();
    public string Name => Script.Name;
    public string State => PivotsLabels.ScriptState(Script.State);
    public string Version => Script.Version.ToString(CultureInfo.CurrentCulture);
    public string Author => Script.Author;
    public string Approver => Script.Approver;
    public string Language => Script.Language;
    public string Guid => Script.Guid;

    /// <summary>The script with its text and its parameter definitions as the site holds them; null until read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Parameters))]
    private SiteScript? _full;

    public string Parameters => Full is not { } full ? NotRead : full.ParametersUnreadable ? "Unreadable" : PivotsLabels.Number(full.Parameters.Count);
    public bool IsApproved => string.Equals(Script.State, SiteScriptStates.Approved, StringComparison.OrdinalIgnoreCase);
    public override string ToString() => $"{Name}, {State}";
}

/// <summary>One saved session of the History tab.</summary>
public sealed class SessionGridRow(PivotsSessionSummary session)
{
    public PivotsSessionSummary Session { get; } = session;
    public string Key { get; } = session.Id;
    public string Started => PivotsLabels.Time(DateTime.SpecifyKind(Session.CreatedUtc, DateTimeKind.Utc));
    public string Action => PivotsLabels.Kind(Session.Kind);
    public string Target => Session.TargetDescription;
    public string Devices => PivotsLabels.Number(Session.DeviceCount);
    public string State => Session.Status;
    public bool CanWaitAgain => Session.OperationId is { Length: > 0 } && Session.Kind is PivotsChangeKind.RunPivot or PivotsChangeKind.RunScript;
    public override string ToString() => $"{Started}, {Action}, {Target}";
}

/// <summary>One audit record of the History tab.</summary>
public sealed class PivotsAuditRow(PivotsAuditRecord record)
{
    public PivotsAuditRecord Record { get; } = record;

    /// <summary>The position of the record in the audit log, oldest first; it orders records of the same second.</summary>
    public int Index { get; init; }

    public string Time => Record.Timestamp.ToString("g", CultureInfo.CurrentCulture);
    public string Action => PivotsLabels.Kind(Record.Event);
    public string Result => Record.Result.Length > 0 ? Record.Result : Record.Phase;
    public string Devices => PivotsLabels.Number(Record.DeviceCount);
    public string Target => Record.Target;
    public string User => Record.User;
    public string Message => Record.Message;
    public string Verified => Record.Verified switch { true => "✓ Verified", false => "! Not verified", null => "" };

    public string VerifiedText => Record.Verified switch
    {
        null => "",
        true => AuditVerification.Verified,
        false when Record.ChainBrokenAt is { } line => AuditVerification.BrokenAt(line),
        false when Record.HasChain => AuditVerification.NotVerified,
        false => AuditVerification.NoChain
    };

    public override string ToString() => $"{Time}, {Action}, {Result}";
}

/// <summary>One run of this page session, as the Pipeline tab lists it; a state change replaces the row.</summary>
public sealed record PipelineRow(int Number, PivotsChangeKind Kind, DateTime StartedAt, int TargetCount, string State, string Results)
{
    public string Key => Number.ToString(CultureInfo.InvariantCulture);
    public string Run => PivotsLabels.Number(Number);
    public string Action => PivotsLabels.Kind(Kind);
    public string Started => PivotsLabels.Time(StartedAt);
    public string Targets => PivotsLabels.Number(TargetCount);
    public override string ToString() => $"{Run}, {Action}, {State}";
}
