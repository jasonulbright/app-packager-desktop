using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The state groups of a (DP, content) row and the labels the page shows for them.</summary>
public static class DpContentLabels
{
    public const string OkLabel = "✓ Installed";
    public const string FailedLabel = "✗ Failed";
    public const string InProgressLabel = "⋯ In progress";
    public const string HealthyLabel = "✓ Healthy";
    public const string NoContentLabel = "⋯ No content";
    public const string UnknownLabel = "? Not known";
    public const string UnknownText = "unknown";

    /// <summary>0 Installed and 8 Content Valid.</summary>
    public static bool IsOk(int state) => state is 0 or 8;

    /// <summary>3 Install Failed and 6 Removal Failed.</summary>
    public static bool IsFailed(int state) => state is 3 or 6;

    /// <summary>1, 2, 4, 5 and 7: pending, retrying and validating.</summary>
    public static bool IsInProgress(int state) => state is 1 or 2 or 4 or 5 or 7;

    /// <summary>4, 5 and 6: the DP is losing or failed to lose the content, so it does not hold a usable copy.</summary>
    public static bool IsRemoval(int state) => state is 4 or 5 or 6;

    public static string PairLabel(int state) => IsOk(state) ? OkLabel : IsFailed(state) ? FailedLabel : IsInProgress(state) ? InProgressLabel : UnknownLabel;

    /// <summary>The label of a pair by the rule the service counts with: an error message makes any state Failed.</summary>
    public static string PairLabel(PairRow pair) => pair.IsFailed ? FailedLabel : IsOk(pair.State) ? OkLabel : pair.IsInProgress ? InProgressLabel : UnknownLabel;

    public static string RollupLabel(int total, int failed, int inProgress, bool known = true) =>
        !known ? UnknownLabel : total == 0 ? NoContentLabel : failed > 0 ? FailedLabel : inProgress > 0 ? InProgressLabel : HealthyLabel;

    public static string TypeLabel(string contentType) => contentType switch
    {
        "Application" => "Application",
        "Package" => "Package",
        "SoftwareUpdatePackage" => "Software update package",
        "BootImage" => "Boot image",
        "OsImage" => "OS image",
        "DriverPackage" => "Driver package",
        "TaskSequence" => "Task sequence",
        { Length: > 0 } other => other,
        _ => UnknownText
    };

    public static readonly IReadOnlyList<string> ContentTypes =
        ["Application", "Package", "SoftwareUpdatePackage", "BootImage", "OsImage", "DriverPackage", "TaskSequence"];

    public static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Percent(double value) => value.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    /// <summary>The key of a pair: the PackageID and the full ServerNALPath, compared without regard to case.</summary>
    public static string Key(PairRow pair) => Key(pair.PackageId, pair.NalPath);

    public static string Key(string packageId, string nalPath) => packageId.ToUpperInvariant() + "|" + nalPath.ToUpperInvariant();

    public static string Count(int count, string singular, string? plural = null) =>
        $"{Number(count)} {(count == 1 ? singular : plural ?? singular + "s")}";
}

/// <summary>One row of the DPs grid.</summary>
public sealed class DpGridRow(DpRow dp)
{
    public DpRow Dp { get; } = dp;
    public string Key { get; } = dp.NalPath.ToUpperInvariant();
    public string Status => DpContentLabels.RollupLabel(Dp.Total, Dp.Failed, Dp.InProgress, Dp.CountsKnown);
    public string DpName => Dp.DpName;
    public string SiteCode => Dp.SiteCode;
    public string Total => Dp.CountsKnown ? DpContentLabels.Number(Dp.Total) : DpContentLabels.UnknownText;
    public string Installed => Dp.CountsKnown ? DpContentLabels.Number(Dp.Installed) : DpContentLabels.UnknownText;
    public string InProgress => Dp.CountsKnown ? DpContentLabels.Number(Dp.InProgress) : DpContentLabels.UnknownText;
    public string Failed => Dp.CountsKnown ? DpContentLabels.Number(Dp.Failed) : DpContentLabels.UnknownText;
    public string PercentOk => Dp.CountsKnown ? DpContentLabels.Percent(Dp.PercentOk) : DpContentLabels.UnknownText;
    public string PullDp => Dp.IsPullDp ? "Yes" : "No";
    public string SizeGb => Dp.TotalSizeGb.ToString("N1", CultureInfo.CurrentCulture);
    public override string ToString() => $"{DpName}, {Status}";
}

/// <summary>One row of the Content grid.</summary>
public sealed class ContentGridRow(ContentRow content)
{
    public ContentRow Content { get; } = content;
    public string Key { get; } = content.PackageId.ToUpperInvariant();
    public string Status => DpContentLabels.RollupLabel(Content.DpsTargeted, Content.Failed, Content.InProgress, Content.CountsKnown);
    public string Name => Content.Name;
    public string Type => DpContentLabels.TypeLabel(Content.ContentType);
    public string PackageId => Content.PackageId;
    public string Size => Content.SizeMb >= 1024
        ? (Content.SizeMb / 1024).ToString("N1", CultureInfo.CurrentCulture) + " GB"
        : Content.SizeMb.ToString("N1", CultureInfo.CurrentCulture) + " MB";
    public string Dps => Count(Content.DpsTargeted);
    public string Installed => Count(Content.Installed);
    public string InProgress => Count(Content.InProgress);
    public string Failed => Count(Content.Failed);
    public string PercentOk => Content.CountsKnown ? DpContentLabels.Percent(Content.PercentOk) : DpContentLabels.UnknownText;
    private string Count(int value) => Content.CountsKnown ? DpContentLabels.Number(value) : DpContentLabels.UnknownText;
    public string Deployments => Content.DeploymentCount < 0 ? DpContentLabels.UnknownText : DpContentLabels.Number(Content.DeploymentCount);
    public override string ToString() => $"{Name} ({PackageId}), {Status}";
}

/// <summary>One (DP, content) row of the per-DP grid, the DP detail and the Status Issues grid.</summary>
public sealed class PairGridRow(PairRow pair)
{
    public PairRow Pair { get; } = pair;
    public string Key { get; } = DpContentLabels.Key(pair);
    public string Status => DpContentLabels.PairLabel(Pair);
    public string DpName => Pair.DpName;
    public string ContentName => Pair.ContentName;
    public string Type => DpContentLabels.TypeLabel(Pair.ContentType);
    public string StateName => Pair.StateText;
    public int State => Pair.State;
    public string PackageId => Pair.PackageId;
    public string LastStatus => Pair.SummaryDate is { } when ? (when.Kind == DateTimeKind.Utc ? when.ToLocalTime() : when).ToString("g", CultureInfo.CurrentCulture) : DpContentLabels.UnknownText;
    public override string ToString() => $"{ContentName} ({PackageId}) on {DpName}, {StateName}";
}

/// <summary>One row of the History grid.</summary>
public sealed class DpContentHistoryRow(DpContentAuditRecord record)
{
    public DpContentAuditRecord Record { get; } = record;

    /// <summary>The position of the record in the audit log, oldest first; it orders records of the same second.</summary>
    public int Index { get; init; }

    public string Time => Record.Timestamp.ToString("g", CultureInfo.CurrentCulture);
    public string Action => DpContentHistoryText.ActionLabel(Record.Action);
    /// <summary>The result; a Started record of a removal has none, so the row shows its phase.</summary>
    public string Result => Record.Result.Length > 0 ? Record.Result : Record.Phase;
    public string Pairs => DpContentLabels.Number(Record.PairCount);
    public string Counts => $"{DpContentLabels.Number(Record.Succeeded)} / {DpContentLabels.Number(Record.Failed)} / {DpContentLabels.Number(Record.Skipped)}";
    public string Content => Record.PackageIds;
    public string Dps => Record.DpNames;
    public string User => Record.User;
    public string Message => Record.Message;
    public string Verified => Record.Verified switch { true => "✓ Verified", false => "! Not verified", null => "" };
    public string VerifiedText => DpContentHistoryText.VerifiedText(Record);
    public override string ToString() => $"{Time}, {Action}, {Result}";
}

public static class DpContentHistoryText
{
    public static string ActionLabel(string action) => action switch
    {
        nameof(DpContentChangeKind.Redistribute) => "Redistribute",
        nameof(DpContentChangeKind.Remove) => "Remove",
        nameof(DpContentChangeKind.Validate) => "Validate",
        nameof(DpContentChangeKind.DistributeAgain) => "Distribute again",
        _ => action
    };

    public static string VerifiedText(DpContentAuditRecord record) => record.Verified switch
    {
        null => "",
        true => AuditVerification.Verified,
        false when record.ChainBrokenAt is { } line => AuditVerification.BrokenAt(line),
        false when record.HasChain => AuditVerification.NotVerified,
        false => AuditVerification.NoChain
    };
}
