using System.Globalization;

namespace ApplicationPackagerEngine.Services;

/// <summary>Builds the changes the page asks PrepareAsync for from records it holds.</summary>
public static class DeploymentChangeBuilder
{
    /// <summary>
    /// The RemoveDeployment that undoes the create of an audit record this product wrote, with the
    /// deployment as the record captured it; null when the record is not a create of this product
    /// that made a deployment, was read from the log without an intact Chain, or lacks a value the
    /// removal compares. The service still re-reads the log and the deployment, compares the
    /// deployment with the record and checks the creator and creation time ConfigMgr holds.
    /// </summary>
    public static DeploymentChange? RemoveFromAudit(DeploymentAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Verified == false) return null;
        if (record.Event != AuditEvents.Create || record.Result is not (AuditResults.Success or AuditResults.Partial)) return null;
        if (TypeOf(record.DeploymentType) is not { } type) return null;
        var id = record.DeploymentUniqueID.Trim();
        var objectId = record.ObjectId.Trim();
        var collectionId = record.CollectionID.Trim().ToUpperInvariant();
        if (!DeploymentService.IsDeploymentId(type, id) || !DeploymentService.IsObjectId(type, objectId) || collectionId.Length != 8 || !collectionId.All(char.IsAsciiLetterOrDigit)) return null;
        if (record.DeployPurpose is not (DeploymentPurposes.Available or DeploymentPurposes.Required) || record.AvailableDateTime is not { } available) return null;
        var required = record.DeployPurpose == DeploymentPurposes.Required;
        if (required && record.DeadlineDateTime is null) return null;
        if (record.TimeBasedOn is not ("Utc" or "LocalTime")) return null;
        if (type == DeploymentObjectType.Package && record.ProgramName.Length == 0) return null;
        var modelId = type == DeploymentObjectType.Application && record.ModelId.Length is > 0 and <= 10 && record.ModelId.All(char.IsAsciiDigit) ? record.ModelId : "";
        var assignment = type is DeploymentObjectType.Application or DeploymentObjectType.SoftwareUpdateGroup
            && long.TryParse(record.DeploymentID, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? record.DeploymentID : "";
        var program = type == DeploymentObjectType.Package ? record.ProgramName : "";

        var existing = new DeploymentRecord(id, type, objectId, record.ApplicationName, collectionId, record.CollectionName, record.DeployPurpose,
            available, required ? record.DeadlineDateTime : null, record.Timestamp, record.User, record.TimeBasedOn == "Utc", program, assignment, modelId);
        var obj = new DeployableObject(type, objectId, record.ApplicationName, type == DeploymentObjectType.Package ? "" : record.ApplicationVersion, program, false, modelId,
            type == DeploymentObjectType.SoftwareUpdateGroup ? UpdateCountOf(record.ApplicationVersion) : 0);
        var collection = new DeploymentCollection(collectionId, record.CollectionName, "", record.MemberCount ?? 0);
        return new DeploymentChange(DeploymentChangeKind.RemoveDeployment, obj, collection, null, [], false, false, false, existing, [],
            [$"Removes deployment {id} of {DeploymentService.ObjectText(obj)} from collection {DeploymentService.Shown(record.CollectionName, collectionId)}.", DeploymentService.UndoNote], id);
    }

    /// <summary>The update count of a software update group record, which this product writes as "(N updates)"; 0 when the text holds none.</summary>
    internal static int UpdateCountOf(string text)
    {
        var value = text.Trim();
        if (!value.StartsWith('(') || !value.EndsWith(" updates)", StringComparison.Ordinal)) return 0;
        return int.TryParse(value[1..^" updates)".Length], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    /// <summary>The object type of a DeploymentType value of the audit log, also as the suite tool writes it.</summary>
    public static DeploymentObjectType? TypeOf(string recorded) =>
        recorded.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "application" or "app" or "apps" => DeploymentObjectType.Application,
            "package" => DeploymentObjectType.Package,
            "tasksequence" => DeploymentObjectType.TaskSequence,
            "softwareupdategroup" or "sug" => DeploymentObjectType.SoftwareUpdateGroup,
            _ => null
        };
}
