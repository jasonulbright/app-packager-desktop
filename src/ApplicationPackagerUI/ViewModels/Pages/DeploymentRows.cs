using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>An object of the Deploy tab's object list.</summary>
public sealed class DeploymentObjectRow(DeployableObject obj)
{
    public DeployableObject Object { get; } = obj;
    public string Name => Object.Name;
    public string Version => Object.Type == DeploymentObjectType.SoftwareUpdateGroup ? DeploymentText.Count(Object.UpdateCount, "update") : Object.Version;
    public string Id => Object.Id;
    public string Packaged => Object.PackagedByProduct ? "Yes" : "";

    public override string ToString() => $"{Name} {Version}".Trim();
}

/// <summary>A device collection of the Deploy tab's collection list; a built-in collection is listed and cannot be chosen.</summary>
public sealed class DeploymentCollectionRow(CollectionRow row)
{
    public CollectionRow Row { get; } = row;
    public string Name => Row.Name;
    public string CollectionId => Row.CollectionId;
    public int MemberCount => Row.MemberCount;
    public string FolderPath => Row.FolderPath.Length == 0 ? "(root)" : Row.FolderPath;
    public bool IsBuiltIn => Row.IsBuiltIn;
    public string Kind => Row.IsBuiltIn ? "Built-in" : "Custom";

    public DeploymentCollection ToCollection() => new(Row.CollectionId, Row.Name, Row.FolderPath, Row.MemberCount);

    public override string ToString() => $"{Name} ({CollectionId})";
}

/// <summary>A distribution point group with its content status for the selected object; checked groups get the content.</summary>
public sealed partial class DpGroupRow(DpGroupInfo info) : ObservableObject
{
    public DpGroupInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Status => Info.Status;
    public int Targeted => Info.Targeted;
    public int Success => Info.Success;
    public int InProgress => Info.InProgress;
    public int Error => Info.Error;

    [ObservableProperty] private bool _isChecked;

    public override string ToString() => $"{Name}, {Status}";
}

/// <summary>One of the five checks as the validation panel shows it.</summary>
public sealed record DeploymentCheckRow(string Name, string Check, string Result, string Message)
{
    public override string ToString() => $"{Check}: {Result}";
}

/// <summary>An audit log record as the History tab shows it.</summary>
public sealed class DeploymentHistoryRow(DeploymentAuditRecord record)
{
    public DeploymentAuditRecord Record { get; } = record;
    public string Time => DeploymentText.Time(Record.Timestamp);
    public string Event => Record.Event;
    public string Type => Record.DeploymentType;
    public string Object => $"{Record.ApplicationName} {Record.ApplicationVersion}".Trim();
    public string Collection => Record.CollectionID.Length > 0 ? $"{Record.CollectionName} ({Record.CollectionID})" : Record.CollectionName;
    public string Purpose => Record.DeployPurpose;
    public string Deadline => Record.DeadlineDateTime is { } d ? DeploymentText.Time(d) + (Basis == "UTC" ? " UTC" : "") : "";

    /// <summary>The time basis of the available time and deadline; empty for a record that does not name it.</summary>
    public string Basis => Record.TimeBasedOn switch
    {
        var b when string.Equals(b, "Utc", StringComparison.OrdinalIgnoreCase) => "UTC",
        var b when string.Equals(b, "LocalTime", StringComparison.OrdinalIgnoreCase) => "Local time",
        _ => ""
    };

    /// <summary>The ID the page and Undo use: the unique ID, or the ID of a record that names no other.</summary>
    public string DeploymentId => Record.DeploymentUniqueID.Length > 0 ? Record.DeploymentUniqueID : Record.DeploymentID;

    /// <summary>The result as a plain word; a Partial create was made with a problem, an Unknown one may exist.</summary>
    public string Result => Record.Result switch
    {
        var r when string.Equals(r, AuditResults.Partial, StringComparison.OrdinalIgnoreCase) => "Partial: made with a problem",
        var r when string.Equals(r, AuditResults.Unknown, StringComparison.OrdinalIgnoreCase) => "Unknown: check ConfigMgr",
        var r => r
    };

    public string User => Record.User;

    /// <summary>Whether the record's chain is intact, as the reports name it; empty for a record that was not read from a log.</summary>
    public string VerifiedText => AuditVerification.Text(Record);

    /// <summary>The Verified cell: a check mark and Verified for an intact record, an exclamation mark and Not verified for any other read record.</summary>
    public string Verified => Record.Verified switch
    {
        true => "✓ " + AuditVerification.Verified,
        false => "! " + AuditVerification.NotVerified,
        null => ""
    };

    /// <summary>The details line: the verified text, the message and the unclear deployment IDs, one per line.</summary>
    public string Details => string.Join(Environment.NewLine, new[]
    {
        VerifiedText,
        Record.Message.Length > 0 ? "Message: " + Record.Message : "",
        Record.UnclearDeploymentIds.Length > 0 ? "Unclear deployment IDs: " + Record.UnclearDeploymentIds : ""
    }.Where(line => line.Length > 0));

    /// <summary>True for a create that made its deployment and whose record carries the deployment ID.</summary>
    public bool CanUndo => string.Equals(Record.Event, AuditEvents.Create, StringComparison.OrdinalIgnoreCase)
        && DeploymentId.Length > 0
        && (string.Equals(Record.Result, AuditResults.Success, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Record.Result, AuditResults.Partial, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Time} {Event} {Object}";
}

/// <summary>A template as the Options tab lists it.</summary>
public sealed class DeploymentTemplateRow(DeploymentTemplate template)
{
    public DeploymentTemplate Template { get; } = template;
    public string Name => Template.Name;
    public string Purpose => Template.Purpose;
    public string Notification => DeploymentText.ChoiceText(DeploymentText.Notifications, Template.UserNotification);
    public string Deadline => Template.DeadlineOffsetHours > 0 ? $"{Template.DeadlineOffsetHours} h after available" : "";
    public string Source => Template.IsSeeded ? "Shipped" : "User";

    public override string ToString() => Name;
}
