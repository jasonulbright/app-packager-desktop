using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>The sections the Reset section buttons clear.</summary>
public enum WorkbenchSection
{
    Install,
    Detection,
    Timing
}

/// <summary>What a Save or Save as did.</summary>
public enum WorkbenchSaveStatus
{
    Saved,
    Cancelled,
    Failed
}

/// <summary>The outcome of a Save or Save as, with the text the window shows.</summary>
public sealed record WorkbenchSaveResult(WorkbenchSaveStatus Status, string Message)
{
    public bool Succeeded => Status == WorkbenchSaveStatus.Saved;
}

/// <summary>
/// The editor state behind the Application Workbench for one application: the working
/// copy of one profile, its saved baseline, the inherited values, draft autosave, and
/// the Save, Save as, Rename and Use defaults rules. The packager default profile is
/// virtual and read-only: saving it always creates a new named profile.
/// </summary>
public sealed class WorkbenchEditorSession
{
    public const string DefaultSaveName = "Managed";

    private static readonly string[] ScriptSectionFields =
        ["Mode", "Command", "ScriptText", "BeforeText", "AfterText", "AfterRunsOnFailure", "ReturnCodes", "RebootPolicy", "WorkingDirectory"];

    private static readonly string[] TimingSectionPaths =
        ["Timing.EstimatedMinutes", "Timing.MaximumMinutes", "Execution.Context",
         "Execution.LogonRequirement", "Execution.UserInteraction", "Execution.ScriptHost"];

    private string _baseline = "";

    public WorkbenchEditorSession(WorkbenchStore store, string applicationId, WorkbenchInheritedSettings inherited)
    {
        Store = store;
        ApplicationId = applicationId;
        Inherited = inherited;
        Profile = WorkbenchProfile.CreateDefault(applicationId);
        _baseline = Canonical(Profile.Data);
    }

    public WorkbenchStore Store { get; }
    public string ApplicationId { get; }
    public WorkbenchInheritedSettings Inherited { get; }

    /// <summary>The working copy. Edits go through the session so drafts and dirty state follow.</summary>
    public WorkbenchProfile Profile { get; private set; }

    public bool IsDefaultProfile => Profile.IsDefault;

    public string ProfileId => Profile.IsDefault ? ApplicationIds.DefaultProfileId : Profile.ProfileId;

    /// <summary>
    /// Set when the profile asked for could not be read and the packager default was loaded in its place;
    /// null after a load that worked. A save of the default then creates a new profile.
    /// </summary>
    public string? LoadWarning { get; private set; }

    /// <summary>A stored draft found when the profile loaded, waiting for Recover or Discard.</summary>
    public JsonObject? PendingDraft { get; private set; }

    /// <summary>True when the working copy differs from the last load or save. Empty objects do not count.</summary>
    public bool IsDirty => Canonical(Profile.Data) != _baseline;

    /// <summary>
    /// Loads a profile; null or empty loads the application's active profile. A profile
    /// that cannot be read falls back to the packager default. A stored draft for a
    /// saved profile becomes <see cref="PendingDraft"/>.
    /// </summary>
    public void Load(string? profileId = null)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            profileId = Store.GetApplicationDefinition(ApplicationId).ActiveProfileId;

        WorkbenchProfile loaded;
        LoadWarning = null;
        try { loaded = Store.GetProfile(ApplicationId, profileId!); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or System.Text.Json.JsonException)
        {
            loaded = WorkbenchProfile.CreateDefault(ApplicationId);
            LoadWarning = $"Profile '{profileId}' could not be read ({ex.Message}); the packager default is shown. Saving creates a new profile.";
        }

        Profile = loaded;
        _baseline = Canonical(Profile.Data);
        PendingDraft = null;
        if (!Profile.IsDefault)
        {
            try { PendingDraft = Store.GetDraft(ApplicationId, Profile.ProfileId); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException) { PendingDraft = null; }
        }
    }

    /// <summary>Replaces the working copy with the pending draft. The draft file stays until a save or discard.</summary>
    public void RecoverDraft()
    {
        if (PendingDraft is null) return;
        var data = WorkbenchJson.CloneObject(PendingDraft);
        data.Remove("IsDefault");
        Profile = new WorkbenchProfile(data);
        PendingDraft = null;
    }

    /// <summary>Deletes the pending draft and keeps the saved profile.</summary>
    public void DiscardPendingDraft()
    {
        if (PendingDraft is null) return;
        PendingDraft = null;
        RemoveDraft(ProfileId);
    }

    /// <summary>Throws the working copy away: the draft is deleted and the saved profile reloads.</summary>
    public void DiscardChanges()
    {
        RemoveDraft(ProfileId);
        Load(ProfileId);
        PendingDraft = null;
    }

    public WorkbenchMember Get(string path) => Profile.Get(path);

    /// <summary>
    /// True when the profile overrides the path. Detection.Mode Inherit, a null InstallMode
    /// and an empty variant split are the new-profile shape and count as inherited.
    /// </summary>
    public bool IsOverridden(string path)
    {
        var member = Profile.Get(path);
        if (!member.Found) return false;
        return path switch
        {
            "Detection.Mode" => !string.Equals(member.Text, "Inherit", StringComparison.OrdinalIgnoreCase),
            "InstallMode" => member.Value is not null,
            "Variants.Split" => WorkbenchJson.IsTruthy(member.Value),
            _ => true
        };
    }

    /// <summary>"Inherited: X", "Custom (inherited X)" or "Custom: removed (inherited X)".</summary>
    public string SourceText(string path, string? inheritedValue = null)
    {
        inheritedValue ??= Inherited.ForPath(path);
        var inheritedText = string.IsNullOrEmpty(inheritedValue) ? "(none)" : inheritedValue;
        if (!IsOverridden(path)) return "Inherited: " + inheritedText;
        return Profile.Get(path).Value is null
            ? "Custom: removed (inherited " + inheritedText + ")"
            : "Custom (inherited " + inheritedText + ")";
    }

    /// <summary>
    /// Writes a text field. Blank text returns the field to inherit, except an explicit
    /// removal, which only the field's Reset clears.
    /// </summary>
    public void SetText(string path, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            var member = Profile.Get(path);
            if (!member.Found || member.Value is not null) Profile.ClearField(path, FieldClearMode.Inherit);
        }
        else
        {
            Profile.Set(path, text);
        }
        Touch();
    }

    public void SetValue(string path, JsonNode? value)
    {
        Profile.Set(path, value);
        Touch();
    }

    /// <summary>Returns one field to inherit.</summary>
    public void ResetField(string path)
    {
        switch (path)
        {
            case "Detection.Mode":
                ResetSection(WorkbenchSection.Detection);
                return;
            case "InstallMode":
                if (Profile.Get(path).Found) Profile.Set(path, null);
                break;
            case "Variants.Split":
                if (Profile.Get(path).Found) Profile.Set(path, new JsonArray());
                break;
            default:
                Profile.ClearField(path, FieldClearMode.Inherit);
                break;
        }
        Touch();
    }

    /// <summary>Stores an explicit none, which wins over the inherited value.</summary>
    public void RemoveField(string path)
    {
        Profile.ClearField(path, FieldClearMode.Explicit);
        Touch();
    }

    /// <summary>
    /// Clears every override on one page. Install covers only the target being edited,
    /// because install and uninstall are configured independently.
    /// </summary>
    public void ResetSection(WorkbenchSection section, string scriptTarget = "Install")
    {
        switch (section)
        {
            case WorkbenchSection.Install:
                foreach (var field in ScriptSectionFields)
                    Profile.ClearField(scriptTarget + "." + field, FieldClearMode.Inherit);
                break;
            case WorkbenchSection.Detection:
                if (Profile.Get("Detection.Mode").Found) Profile.Set("Detection.Mode", "Inherit");
                Profile.ClearField("Detection.Rule", FieldClearMode.Inherit);
                Profile.ClearField("Detection.IntuneScriptConversion", FieldClearMode.Inherit);
                break;
            case WorkbenchSection.Timing:
                foreach (var path in TimingSectionPaths) Profile.ClearField(path, FieldClearMode.Inherit);
                break;
        }
        Touch();
    }

    /// <summary>Clears every override in the profile; its id, name and revision stay.</summary>
    public void UseDefaults()
    {
        if (Profile.IsDefault)
        {
            Profile = WorkbenchProfile.CreateDefault(ApplicationId);
        }
        else
        {
            var fresh = WorkbenchProfile.CreateNew(ApplicationId, Profile.ProfileId, Profile.Name);
            fresh.Revision = Profile.Revision;
            Profile = fresh;
        }
        Touch();
    }

    /// <summary>Sets the profile's display name. A blank name is stored as Custom at save time.</summary>
    public void SetProfileName(string? name)
    {
        Profile.Name = name?.Trim() ?? "";
        Touch();
    }

    /// <summary>Why Rename is refused, or null when the profile can be renamed.</summary>
    public string? RenameRefusal() => Profile.IsDefault
        ? "The packager default profile cannot be renamed. Use Save as to create a named profile."
        : null;

    /// <summary>
    /// Save (no name) updates the active saved profile; Save as (a name) copies the
    /// working copy, and its managed assets, under a new profile id. Saving the packager
    /// default asks for a name through <paramref name="askName"/> and creates a profile,
    /// because the default is never stored. The working copy is validated first; a
    /// refusal leaves nothing written. The saved profile becomes the active one, chosen
    /// icon and non-linked source files are copied into profile storage, and drafts for
    /// the saved profile are removed.
    /// </summary>
    public WorkbenchSaveResult Save(string? saveAsName = null, Func<string, string, string?>? askName = null)
    {
        var data = WorkbenchJson.CloneObject(Profile.Data);
        data.Remove("IsDefault");
        if (string.IsNullOrWhiteSpace(WorkbenchJson.AsText(data["Name"]))) data["Name"] = "Custom";

        try { WorkbenchProfileValidator.Validate(data); }
        catch (InvalidOperationException ex) { return new WorkbenchSaveResult(WorkbenchSaveStatus.Failed, ex.Message); }

        var sourceProfileId = ProfileId;
        var sourceWasDefault = Profile.IsDefault;
        if (!string.IsNullOrWhiteSpace(saveAsName) || sourceWasDefault)
        {
            var name = saveAsName;
            if (string.IsNullOrWhiteSpace(name))
                name = askName?.Invoke("Name for this profile", DefaultSaveName);
            if (string.IsNullOrWhiteSpace(name)) return new WorkbenchSaveResult(WorkbenchSaveStatus.Cancelled, "");

            var newId = Guid.NewGuid().ToString("N");
            data["ProfileId"] = newId;
            data["ApplicationId"] = ApplicationId;
            data["Name"] = name.Trim();
            data["Revision"] = 0;
            if (!sourceWasDefault) data["CopiedFrom"] = sourceProfileId;
            data.Remove("SavedAt");
        }

        var working = new WorkbenchProfile(data);
        WorkbenchProfile saved;
        try
        {
            if (!sourceWasDefault && !string.Equals(working.ProfileId, sourceProfileId, StringComparison.OrdinalIgnoreCase))
                CopyAssets(sourceProfileId, working.ProfileId);
            Store.ImportProfileAssets(working);
            saved = Store.SaveProfile(working, setActive: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new WorkbenchSaveResult(WorkbenchSaveStatus.Failed, ex.Message);
        }

        if (!sourceWasDefault) RemoveDraft(sourceProfileId);
        RemoveDraft(saved.ProfileId);
        Profile = saved;
        _baseline = Canonical(Profile.Data);
        PendingDraft = null;
        return new WorkbenchSaveResult(WorkbenchSaveStatus.Saved,
            $"Saved \"{saved.Name}\" at revision {saved.Revision}. Existing builds are now stale relative to this profile; nothing was deployed.");
    }

    /// <summary>
    /// Keeps the draft in step with the working copy: a dirty saved profile is written to
    /// its draft file and a clean one has none. The packager default keeps no draft.
    /// </summary>
    private void Touch()
    {
        if (Profile.IsDefault) return;
        try
        {
            if (IsDirty) Store.SaveDraft(ApplicationId, Profile.ProfileId, Profile.Data);
            else Store.RemoveDraft(ApplicationId, Profile.ProfileId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void RemoveDraft(string profileId)
    {
        if (ApplicationIds.IsDefaultProfile(profileId)) return;
        try { Store.RemoveDraft(ApplicationId, profileId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void CopyAssets(string fromProfileId, string toProfileId)
    {
        var source = Path.Combine(Store.GetProfileFolder(ApplicationId, fromProfileId), "assets");
        if (!Directory.Exists(source)) return;
        var target = Path.Combine(Store.GetProfileFolder(ApplicationId, toProfileId, create: true), "assets");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>
    /// The document as compared for dirty state: keys sorted, empty objects and the
    /// SavedAt and IsDefault bookkeeping removed, so an edit that is undone reads clean.
    /// </summary>
    internal static string Canonical(JsonObject data)
    {
        var copy = WorkbenchJson.CloneObject(data);
        copy.Remove("SavedAt");
        copy.Remove("IsDefault");
        return Normalize(copy)?.ToJsonString() ?? "{}";
    }

    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var child = Normalize(value);
                    if (child is JsonObject { Count: 0 }) continue;
                    sorted[key] = child;
                }
                return sorted;
            case JsonArray array:
                return new JsonArray(array.Select(Normalize).ToArray());
            default:
                return node?.DeepClone();
        }
    }
}
