using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The state groups of a (DP, content) row and the labels the page shows for them.</summary>
public static class DpContentStates
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
    public string Status => DpContentStates.RollupLabel(Dp.Total, Dp.Failed, Dp.InProgress, Dp.CountsKnown);
    public string DpName => Dp.DpName;
    public string SiteCode => Dp.SiteCode;
    public string Total => Dp.CountsKnown ? DpContentStates.Number(Dp.Total) : DpContentStates.UnknownText;
    public string Installed => Dp.CountsKnown ? DpContentStates.Number(Dp.Installed) : DpContentStates.UnknownText;
    public string InProgress => Dp.CountsKnown ? DpContentStates.Number(Dp.InProgress) : DpContentStates.UnknownText;
    public string Failed => Dp.CountsKnown ? DpContentStates.Number(Dp.Failed) : DpContentStates.UnknownText;
    public string PercentOk => Dp.CountsKnown ? DpContentStates.Percent(Dp.PercentOk) : DpContentStates.UnknownText;
    public string PullDp => Dp.IsPullDp ? "Yes" : "No";
    public string SizeGb => Dp.TotalSizeGb.ToString("N1", CultureInfo.CurrentCulture);
    public override string ToString() => $"{DpName}, {Status}";
}

/// <summary>One row of the Content grid.</summary>
public sealed class ContentGridRow(ContentRow content)
{
    public ContentRow Content { get; } = content;
    public string Key { get; } = content.PackageId.ToUpperInvariant();
    public string Status => DpContentStates.RollupLabel(Content.DpsTargeted, Content.Failed, Content.InProgress);
    public string Name => Content.Name;
    public string Type => DpContentStates.TypeLabel(Content.ContentType);
    public string PackageId => Content.PackageId;
    public string Size => Content.SizeMb >= 1024
        ? (Content.SizeMb / 1024).ToString("N1", CultureInfo.CurrentCulture) + " GB"
        : Content.SizeMb.ToString("N1", CultureInfo.CurrentCulture) + " MB";
    public string Dps => DpContentStates.Number(Content.DpsTargeted);
    public string Installed => DpContentStates.Number(Content.Installed);
    public string InProgress => DpContentStates.Number(Content.InProgress);
    public string Failed => DpContentStates.Number(Content.Failed);
    public string PercentOk => DpContentStates.Percent(Content.PercentOk);
    public string Deployments => Content.DeploymentCount < 0 ? DpContentStates.UnknownText : DpContentStates.Number(Content.DeploymentCount);
    public override string ToString() => $"{Name} ({PackageId}), {Status}";
}

/// <summary>One (DP, content) row of the per-DP grid, the DP detail and the Status Issues grid.</summary>
public sealed class PairGridRow(PairRow pair)
{
    public PairRow Pair { get; } = pair;
    public string Key { get; } = DpContentStates.Key(pair);
    public string Status => DpContentStates.PairLabel(Pair.State);
    public string DpName => Pair.DpName;
    public string ContentName => Pair.ContentName;
    public string Type => DpContentStates.TypeLabel(Pair.ContentType);
    public string StateName => Pair.StateName;
    public int State => Pair.State;
    public string PackageId => Pair.PackageId;
    public string LastStatus => Pair.SummaryDate is { } when ? when.ToString("g", CultureInfo.CurrentCulture) : DpContentStates.UnknownText;
    public override string ToString() => $"{ContentName} ({PackageId}) on {DpName}, {StateName}";
}

/// <summary>One row of the History grid.</summary>
public sealed class DpContentHistoryRow(DpContentAuditRecord record)
{
    public DpContentAuditRecord Record { get; } = record;
    public string Time => Record.Timestamp.ToString("g", CultureInfo.CurrentCulture);
    public string Action => DpContentHistoryText.ActionLabel(Record.Action);
    public string Result => Record.Result;
    public string Pairs => DpContentStates.Number(Record.PairCount);
    public string Counts => $"{DpContentStates.Number(Record.Succeeded)} / {DpContentStates.Number(Record.Failed)} / {DpContentStates.Number(Record.Skipped)}";
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
