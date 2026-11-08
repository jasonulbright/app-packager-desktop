using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class WorkbenchEditorSessionTests : IDisposable
{
    private const string App = "catalog:package-editor";
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private WorkbenchEditorSession NewSession(string? profileId = null)
    {
        var inherited = WorkbenchInheritedSettings.Build("Editor App", "Contoso", "", 15, 30);
        var session = new WorkbenchEditorSession(_t.Store, App, inherited);
        session.Load(profileId);
        return session;
    }

    private static Func<string, string, string?> Answer(string? name) => (_, _) => name;

    [Fact]
    public void AnUntouchedField_ReportsTheInheritedValue()
    {
        var session = NewSession();

        Assert.True(session.IsDefaultProfile);
        Assert.False(session.IsOverridden("Application.DisplayName"));
        Assert.Equal("Inherited: Editor App", session.SourceText("Application.DisplayName"));
        Assert.Equal("Inherited: Contoso", session.SourceText("Application.Publisher"));
        Assert.Equal("Inherited: (none)", session.SourceText("Application.Description"));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Editing_MarksTheFieldCustomAndDirty_AndResetReturnsItToInheritedAndClean()
    {
        var session = NewSession();

        session.SetText("Application.DisplayName", "Probe Title");

        Assert.True(session.IsOverridden("Application.DisplayName"));
        Assert.Equal("Custom (inherited Editor App)", session.SourceText("Application.DisplayName"));
        Assert.True(session.IsDirty);

        session.ResetField("Application.DisplayName");

        Assert.False(session.IsOverridden("Application.DisplayName"));
        Assert.Equal("Inherited: Editor App", session.SourceText("Application.DisplayName"));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void BlankText_ReturnsToInherit_ButKeepsAnExplicitRemovalUntilReset()
    {
        var session = NewSession();
        session.SetText("Application.Publisher", "Someone");
        session.SetText("Application.Publisher", "  ");
        Assert.False(session.IsOverridden("Application.Publisher"));

        session.RemoveField("Application.Icon");
        Assert.Equal("Custom: removed (inherited (none))", session.SourceText("Application.Icon", ""));
        session.SetText("Application.Icon", "");
        Assert.True(session.IsOverridden("Application.Icon"));

        session.ResetField("Application.Icon");
        Assert.False(session.IsOverridden("Application.Icon"));
    }

    [Fact]
    public void SavingTheDefaultProfile_AsksForANameAndCreatesAnActiveProfileHoldingOnlyTheOverride()
    {
        var session = NewSession();
        session.SetText("Application.DisplayName", "Probe Managed Title");
        string? asked = null;

        var result = session.Save(askName: (title, value) => { asked = $"{title}|{value}"; return "Probe Managed"; });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Name for this profile|Managed", asked);
        Assert.False(session.IsDefaultProfile);
        Assert.NotEqual(ApplicationIds.DefaultProfileId, session.ProfileId);
        Assert.False(session.IsDirty);
        Assert.Equal(session.ProfileId, _t.Store.GetApplicationDefinition(App).ActiveProfileId);
        Assert.StartsWith("Saved \"Probe Managed\" at revision 1.", result.Message);

        var stored = (JsonObject)WorkbenchJson.Read(Path.Combine(_t.Store.GetProfileFolder(App, session.ProfileId), "profile.json"))!;
        var application = Assert.IsType<JsonObject>(stored["Application"]);
        Assert.Equal(["DisplayName"], application.Select(p => p.Key));
        Assert.Equal("Probe Managed Title", application["DisplayName"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonObject>(stored["Timing"]));
        Assert.Empty(Assert.IsType<JsonObject>(stored["Execution"]));
        Assert.Empty(Assert.IsType<JsonObject>(stored["Install"]));
        Assert.Equal("Inherit", stored["Detection"]!["Mode"]!.GetValue<string>());
    }

    [Fact]
    public void ACancelledNamePrompt_WritesNothing()
    {
        var session = NewSession();
        session.SetText("Application.DisplayName", "x");

        var result = session.Save(askName: Answer(null));

        Assert.Equal(WorkbenchSaveStatus.Cancelled, result.Status);
        Assert.True(session.IsDefaultProfile);
        Assert.Single(_t.Store.GetProfiles(App));
    }

    [Fact]
    public void Save_UpdatesTheSameProfileAndRaisesItsRevision()
    {
        var session = NewSession();
        session.SetText("Application.DisplayName", "First");
        session.Save(askName: Answer("Managed"));
        var id = session.ProfileId;

        session.SetText("Application.DisplayName", "Second");
        var result = session.Save();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(id, session.ProfileId);
        Assert.Equal(2, session.Profile.Revision);
        Assert.Equal("Second", _t.Store.GetProfile(App, id).Get("Application.DisplayName").Text);
        Assert.Equal(2, _t.Store.GetProfiles(App).Count);
    }

    [Fact]
    public void SaveAs_CopiesTheWorkingCopyAndItsAssetsUnderANewIdAndLeavesTheSourceUnchanged()
    {
        var session = NewSession();
        var icon = _t.NewFile(Path.Combine(_t.NewFolder(), "icon.png"), "png-bytes");
        session.SetValue("Application.Icon", new JsonObject { ["Path"] = icon, ["Asset"] = "" });
        session.Save(askName: Answer("Managed"));
        var sourceId = session.ProfileId;
        var assetId = session.Get("Application.Icon.Asset").Text!;

        session.SetText("Application.Publisher", "Lab publisher");
        var result = session.Save(saveAsName: "Lab");

        Assert.True(result.Succeeded, result.Message);
        Assert.NotEqual(sourceId, session.ProfileId);
        Assert.Equal("Lab", session.Profile.Name);
        Assert.Equal(1, session.Profile.Revision);
        Assert.Equal(sourceId, session.Get("CopiedFrom").Text);
        Assert.Null(_t.Store.GetProfile(App, sourceId).Get("Application.Publisher").Text);
        Assert.True(File.Exists(_t.Store.ResolveAssetPath(App, session.ProfileId, assetId)));
        Assert.Equal(session.ProfileId, _t.Store.GetApplicationDefinition(App).ActiveProfileId);
    }

    [Fact]
    public void TheDefaultProfile_IsNeverStoredOrRenamed()
    {
        var session = NewSession();
        Assert.NotNull(session.RenameRefusal());
        Assert.Throws<InvalidOperationException>(() => _t.Store.SaveProfile(session.Profile));

        session.SetText("Application.DisplayName", "x");
        session.Save(askName: Answer("Managed"));

        Assert.Null(session.RenameRefusal());
        Assert.DoesNotContain(_t.Store.GetProfiles(App), p => !p.IsDefault && ApplicationIds.IsDefaultProfile(p.ProfileId));
        Assert.False(Directory.Exists(Path.Combine(_t.Store.GetApplicationFolder(App), "profiles", "default")));
    }

    [Fact]
    public void AnEstimateAboveTheMaximum_BlocksSaveWithTheValidatorMessage()
    {
        var session = NewSession();
        session.SetValue("Timing.EstimatedMinutes", 60);
        session.SetValue("Timing.MaximumMinutes", 30);

        var result = session.Save(askName: Answer("Managed"));

        Assert.Equal(WorkbenchSaveStatus.Failed, result.Status);
        Assert.Equal("Timing.EstimatedMinutes (60) cannot exceed Timing.MaximumMinutes (30).", result.Message);
        Assert.True(session.IsDefaultProfile);
        Assert.True(session.IsDirty);
        Assert.Single(_t.Store.GetProfiles(App));
    }

    [Fact]
    public void EditingASavedProfile_KeepsADraftThatANewSessionOffersToRecover()
    {
        var first = NewSession();
        first.SetText("Application.DisplayName", "Saved");
        first.Save(askName: Answer("Managed"));
        var id = first.ProfileId;

        first.SetText("Application.DisplayName", "Unsaved edit");
        Assert.NotNull(_t.Store.GetDraft(App, id));

        var second = NewSession();
        Assert.Equal(id, second.ProfileId);
        Assert.NotNull(second.PendingDraft);
        Assert.Equal("Saved", second.Get("Application.DisplayName").Text);

        second.RecoverDraft();

        Assert.Equal("Unsaved edit", second.Get("Application.DisplayName").Text);
        Assert.True(second.IsDirty);
        Assert.Null(second.PendingDraft);
    }

    [Fact]
    public void UndoingAnEdit_RemovesTheDraft_AndDiscardDeletesIt()
    {
        var session = NewSession();
        session.SetText("Application.DisplayName", "Saved");
        session.Save(askName: Answer("Managed"));
        var id = session.ProfileId;

        session.SetText("Application.Publisher", "edit");
        Assert.NotNull(_t.Store.GetDraft(App, id));
        session.ResetField("Application.Publisher");
        Assert.Null(_t.Store.GetDraft(App, id));

        session.SetText("Application.Publisher", "edit");
        session.DiscardChanges();
        Assert.Null(_t.Store.GetDraft(App, id));
        Assert.False(session.IsDirty);
        Assert.Null(session.Get("Application.Publisher").Text);

        session.SetText("Application.Publisher", "edit again");
        var reopened = NewSession();
        reopened.DiscardPendingDraft();
        Assert.Null(_t.Store.GetDraft(App, id));
    }

    [Fact]
    public void EditsToTheDefaultProfile_WriteNoDraft()
    {
        var session = NewSession();
        session.SetText("Application.DisplayName", "x");

        Assert.False(Directory.Exists(_t.Store.DraftsRoot));
    }

    [Fact]
    public void ResetSection_ClearsOnlyThatPage_AndInstallOnlyTheEditedTarget()
    {
        var session = NewSession();
        session.SetValue("Install.Mode", "Custom");
        session.SetText("Install.ScriptText", "Write-Output 1");
        session.SetValue("Uninstall.Mode", "Extend");
        session.SetValue("Detection.Mode", "Custom");
        session.SetValue("Detection.Rule", new WorkbenchDetectionRule().ToJson());
        session.SetValue("Timing.EstimatedMinutes", 5);
        session.SetText("Execution.Context", "User");
        session.SetText("Application.DisplayName", "kept");

        session.ResetSection(WorkbenchSection.Install, "Install");
        Assert.False(session.IsOverridden("Install.Mode"));
        Assert.False(session.IsOverridden("Install.ScriptText"));
        Assert.True(session.IsOverridden("Uninstall.Mode"));

        session.ResetSection(WorkbenchSection.Detection);
        Assert.False(session.IsOverridden("Detection.Mode"));
        Assert.False(session.Get("Detection.Rule").Found);

        session.ResetSection(WorkbenchSection.Timing);
        Assert.False(session.IsOverridden("Timing.EstimatedMinutes"));
        Assert.False(session.IsOverridden("Execution.Context"));
        Assert.True(session.IsOverridden("Application.DisplayName"));
    }

    [Fact]
    public void UseDefaults_ClearsEveryOverrideButKeepsTheProfileIdentity()
    {
        var session = NewSession();
        session.SetText("Application.DisplayName", "x");
        session.Save(askName: Answer("Managed"));
        var id = session.ProfileId;
        session.SetText("Application.Publisher", "y");

        session.UseDefaults();

        Assert.Equal(id, session.ProfileId);
        Assert.Equal("Managed", session.Profile.Name);
        Assert.Equal(1, session.Profile.Revision);
        Assert.False(session.IsOverridden("Application.DisplayName"));
        Assert.False(session.IsOverridden("Application.Publisher"));
        Assert.True(session.IsDirty);
        Assert.True(session.Save().Succeeded);
    }

    [Fact]
    public void Save_ImportsAChosenIconAndUnlinkedSourceFilesIntoProfileStorage()
    {
        var session = NewSession();
        var folder = _t.NewFolder();
        var icon = _t.NewFile(Path.Combine(folder, "app.ico"), "ico");
        var config = _t.NewFile(Path.Combine(folder, "config.xml"), "<x/>");
        var linked = _t.NewFile(Path.Combine(folder, "linked.txt"), "linked");
        session.SetValue("Application.Icon", new JsonObject { ["Path"] = icon, ["Asset"] = "" });
        var files = WorkbenchSourceFiles.FromPath(config).Concat(WorkbenchSourceFiles.FromPath(linked).Select(f => f with { Linked = true }));
        session.SetValue("SourceFiles", WorkbenchSourceFiles.ToJson(files));

        var result = session.Save(askName: Answer("Managed"));

        Assert.True(result.Succeeded, result.Message);
        var iconAsset = session.Get("Application.Icon.Asset").Text;
        Assert.Matches("^[0-9a-f]{16}$", iconAsset);
        Assert.StartsWith(_t.Store.GetProfileFolder(App, session.ProfileId), session.Get("Application.Icon.Path").Text);
        var entries = WorkbenchSourceFiles.Read(session.Get("SourceFiles").Value);
        Assert.Matches("^[0-9a-f]{16}$", entries[0].Asset);
        Assert.True(File.Exists(_t.Store.ResolveAssetPath(App, session.ProfileId, entries[0].Asset)));
        Assert.Equal("", entries[1].Asset);
        Assert.True(entries[1].Linked);
    }

    [Fact]
    public void AMissingActiveProfile_LoadsThePackagerDefault()
    {
        _t.Store.SetActiveProfile(App, "0123456789abcdef0123456789abcdef");

        var session = NewSession();

        Assert.True(session.IsDefaultProfile);
        Assert.Equal(WorkbenchProfile.DefaultProfileName, session.Profile.Name);
    }

    [Fact]
    public void TheNewProfileShape_CountsAsInherited()
    {
        var session = NewSession();

        Assert.False(session.IsOverridden("Detection.Mode"));
        Assert.False(session.IsOverridden("InstallMode"));
        Assert.False(session.IsOverridden("Variants.Split"));

        session.SetValue("InstallMode", "AllUsers");
        session.SetValue("Variants.Split", WorkbenchFieldMapping.DisplayToVariantSplit("Architecture"));
        Assert.True(session.IsOverridden("InstallMode"));
        Assert.True(session.IsOverridden("Variants.Split"));

        session.ResetField("InstallMode");
        session.ResetField("Variants.Split");
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void AProfileThatCannotBeReadFallsBackToTheDefaultAndSaysSo()
    {
        var saved = _t.NewProfile(App, "Broken");
        File.WriteAllText(Path.Combine(_t.Store.GetProfileFolder(App, saved.ProfileId), "profile.json"), "{ \"SchemaVersion\": 99 }");

        var session = NewSession(saved.ProfileId);

        Assert.True(session.IsDefaultProfile);
        Assert.NotNull(session.LoadWarning);
        Assert.Contains(saved.ProfileId, session.LoadWarning);
        Assert.Contains("default", session.LoadWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AProfileThatLoadsHasNoLoadWarning_AndALaterLoadClearsAnEarlierOne()
    {
        var good = _t.NewProfile(App, "Good");
        var broken = _t.NewProfile(App, "Broken");
        File.WriteAllText(Path.Combine(_t.Store.GetProfileFolder(App, broken.ProfileId), "profile.json"), "{ \"SchemaVersion\": 99 }");
        var session = NewSession(broken.ProfileId);
        Assert.NotNull(session.LoadWarning);

        session.Load(good.ProfileId);

        Assert.Null(session.LoadWarning);
        Assert.False(session.IsDefaultProfile);
    }

    [Fact]
    public void ASuccessfulSaveClearsTheLoadWarningAndAFailedSaveKeepsIt()
    {
        var broken = _t.NewProfile(App, "Broken");
        File.WriteAllText(Path.Combine(_t.Store.GetProfileFolder(App, broken.ProfileId), "profile.json"), "{ \"SchemaVersion\": 99 }");
        var session = NewSession(broken.ProfileId);
        Assert.NotNull(session.LoadWarning);

        var cancelled = session.Save(askName: (_, _) => null);
        Assert.Equal(WorkbenchSaveStatus.Cancelled, cancelled.Status);
        Assert.NotNull(session.LoadWarning);

        var saved = session.Save(askName: (_, _) => "Replacement");
        Assert.Equal(WorkbenchSaveStatus.Saved, saved.Status);
        Assert.Null(session.LoadWarning);
    }

    [Fact]
    public void TheDefaultProfileHasNoLoadWarning() => Assert.Null(NewSession().LoadWarning);
}
