using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Models.Workbench;

/// <summary>How <see cref="WorkbenchProfile.ClearField"/> clears a field.</summary>
public enum FieldClearMode
{
    /// <summary>Removes the key, so the next precedence level supplies the value.</summary>
    Inherit,

    /// <summary>Stores an explicit null, which means none and still wins.</summary>
    Explicit
}

/// <summary>
/// A named set of overrides for one application. The data is a sparse JSON object:
/// an absent key inherits from the next precedence level, and a key holding JSON
/// null is an explicit none. A typed object with nullable properties cannot carry
/// that difference, so every override field is read through <see cref="Get"/>.
/// </summary>
public sealed class WorkbenchProfile
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultProfileName = "Packager default";

    public WorkbenchProfile(JsonObject data, bool isDefault = false)
    {
        Data = data;
        IsDefault = isDefault;
    }

    /// <summary>The stored profile document. Keys compare case-insensitively.</summary>
    public JsonObject Data { get; }

    /// <summary>True for the virtual default profile, which is never stored.</summary>
    public bool IsDefault { get; }

    public string ProfileId
    {
        get => WorkbenchJson.AsText(Data["ProfileId"]) ?? "";
        set => Data["ProfileId"] = value;
    }

    public string ApplicationId
    {
        get => WorkbenchJson.AsText(Data["ApplicationId"]) ?? "";
        set => Data["ApplicationId"] = value;
    }

    public string Name
    {
        get => WorkbenchJson.AsText(Data["Name"]) ?? "";
        set => Data["Name"] = value;
    }

    public int Revision
    {
        get => WorkbenchJson.AsInt(Data["Revision"]) ?? 0;
        set => Data["Revision"] = value;
    }

    public int SchemaVersion => WorkbenchJson.AsInt(Data["SchemaVersion"]) ?? 0;

    /// <summary>Reads a dotted field path such as Install.Command or Timing.EstimatedMinutes.</summary>
    public WorkbenchMember Get(string path) => WorkbenchJson.Get(Data, path);

    public void Set(string path, JsonNode? value) => WorkbenchJson.Set(Data, path, value);

    /// <summary>Clears one field to inherit, or sets it to an explicit none.</summary>
    public void ClearField(string path, FieldClearMode mode)
    {
        if (mode == FieldClearMode.Inherit) WorkbenchJson.Remove(Data, path);
        else WorkbenchJson.Set(Data, path, null);
    }

    public WorkbenchProfile Clone() => new(WorkbenchJson.CloneObject(Data), IsDefault);

    /// <summary>
    /// The new-profile shape: every section present and empty, Detection on Inherit.
    /// A null or empty profile id gets a new random id.
    /// </summary>
    public static WorkbenchProfile CreateNew(string applicationId, string? profileId = null, string name = "Custom")
    {
        if (string.IsNullOrWhiteSpace(profileId)) profileId = Guid.NewGuid().ToString("N");
        var data = WorkbenchJson.NewObject();
        data["SchemaVersion"] = CurrentSchemaVersion;
        data["ProfileId"] = profileId;
        data["ApplicationId"] = applicationId;
        data["Name"] = name;
        data["Revision"] = 0;
        data["BaseProviderHash"] = null;
        data["PinnedVersion"] = null;
        data["Application"] = WorkbenchJson.NewObject();
        data["Install"] = WorkbenchJson.NewObject();
        data["Uninstall"] = WorkbenchJson.NewObject();
        var detection = WorkbenchJson.NewObject();
        detection["Mode"] = "Inherit";
        data["Detection"] = detection;
        var requirements = WorkbenchJson.NewObject();
        requirements["Operations"] = new JsonArray();
        data["Requirements"] = requirements;
        var variants = WorkbenchJson.NewObject();
        variants["Split"] = new JsonArray();
        variants["Overrides"] = WorkbenchJson.NewObject();
        data["Variants"] = variants;
        data["InstallMode"] = null;
        data["Timing"] = WorkbenchJson.NewObject();
        data["Execution"] = WorkbenchJson.NewObject();
        data["SourceFiles"] = new JsonArray();
        data["Tokens"] = WorkbenchJson.NewObject();
        data["Assets"] = WorkbenchJson.NewObject();
        return new WorkbenchProfile(data);
    }

    /// <summary>The virtual default profile: the new-profile shape with no override and revision 0.</summary>
    public static WorkbenchProfile CreateDefault(string applicationId)
    {
        var profile = CreateNew(applicationId, ApplicationIds.DefaultProfileId, DefaultProfileName);
        return new WorkbenchProfile(profile.Data, isDefault: true);
    }
}

/// <summary>One row of a profile listing.</summary>
public sealed record WorkbenchProfileSummary(
    string ProfileId,
    string ApplicationId,
    string Name,
    int Revision,
    bool IsDefault,
    bool IsActive,
    string? Path);
