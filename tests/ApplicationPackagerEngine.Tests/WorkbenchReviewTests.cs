using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class WorkbenchReviewTests : IDisposable
{
    private const string App = "catalog:package-review";
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void TheInheritedLevel_ResolvesGlobalsAndReportsStageOnlyFields()
    {
        var inherited = WorkbenchInheritedSettings.Build("", null, "A description", 20, 45);

        Assert.Equal(WorkbenchInheritedSettings.NeedsStagingMarker, inherited.DisplayName);
        Assert.Equal(WorkbenchInheritedSettings.NeedsStagingMarker, inherited.InstallCommand);
        Assert.Equal(WorkbenchInheritedSettings.NeedsStagingMarker, inherited.DetectionSummary);
        Assert.Equal("A description", inherited.Description);
        Assert.Equal(20, inherited.EstimatedMinutes);
        Assert.Equal(45, inherited.MaximumMinutes);
        Assert.Equal("System", inherited.ExecutionContext);
        Assert.Equal("Packager default", inherited.TitleMode);
        Assert.Equal("Include version", WorkbenchInheritedSettings.Build("x", "y", "", 1, 2, includeVersionInTitle: true).TitleMode);
    }

    [Fact]
    public void TheDiff_ListsOnlyOverriddenFields_WithTheInheritedValue()
    {
        var session = new WorkbenchEditorSession(_t.Store, App, WorkbenchInheritedSettings.Build("Review App", "Contoso", "", 15, 30));
        session.Load();
        session.SetText("Application.DisplayName", "Custom Title");
        session.SetValue("Timing.MaximumMinutes", 90);
        session.SetValue("Install.Mode", "Custom");
        session.SetValue("SourceFiles", WorkbenchSourceFiles.ToJson([new WorkbenchSourceFile("", "c:\\a", "a", "", 1, false)]));

        var rows = WorkbenchReview.Diff(session);

        Assert.Equal(
        [
            new WorkbenchDiffRow("Application.DisplayName", "Review App", "Custom Title"),
            new WorkbenchDiffRow("Install.Mode", "Generated", "Custom"),
            new WorkbenchDiffRow("Timing.MaximumMinutes", "30", "90"),
            new WorkbenchDiffRow("SourceFiles", "(none)", "1 file(s)")
        ], rows);

        var plan = WorkbenchReview.Plan(session, "signing service not loaded; no script will be signed in this build");
        Assert.Contains("Title        : Custom Title", plan);
        Assert.Contains("Timing       : estimated 15 min, maximum 90 min", plan);
        Assert.Contains("Detection    : (needs staging)", plan);
        Assert.Contains("Profile      : Packager default (default, revision 0)", plan);
    }

    [Fact]
    public void TheDiff_ListsScriptAndHookTextAsALineCount()
    {
        var session = new WorkbenchEditorSession(_t.Store, App, WorkbenchInheritedSettings.Build("Review App", "Contoso", "", 15, 30));
        session.Load();
        session.SetValue("Install.Mode", "Extend");
        session.SetText("Install.BeforeText", "New-Item C:\\LabTest\r\nSet-Content C:\\LabTest\\a.txt 1");
        session.SetText("Uninstall.AfterText", "exit 0");

        var rows = WorkbenchReview.Diff(session);

        Assert.Equal(
        [
            new WorkbenchDiffRow("Install.Mode", "Generated", "Extend"),
            new WorkbenchDiffRow("Install.BeforeText", "(none)", "2 line(s)"),
            new WorkbenchDiffRow("Uninstall.AfterText", "(none)", "1 line(s)")
        ], rows);
    }

    [Fact]
    public void ABridgeForAnExistingSnapshot_UsesThatSnapshotsProfileNotTheActiveOne()
    {
        var active = _t.NewProfile(App, "Active");
        var other = WorkbenchProfile.CreateNew(App, name: "Other");
        other.Set("Install.Command", "setup.exe /other");
        other = _t.Store.SaveProfile(other);
        _t.Store.SetActiveProfile(App, active.ProfileId);
        var downloadRoot = WorkbenchPaths.GetProfileDownloadRoot(@"C:\dl", other.ProfileId);
        var snapshot = _t.Store.CreateRunSnapshot(new RunSnapshotRequest
        {
            ApplicationId = App, ProfileId = other.ProfileId, Target = RunTargets.IntuneOnly, DownloadRoot = downloadRoot
        });

        var launch = PackagerChildBridge.FromSnapshot(_t.Store, snapshot, @"C:\dl");

        Assert.Equal(other.ProfileId, launch.ProfileId);
        Assert.Equal(downloadRoot, launch.DownloadRoot);
        Assert.Equal(snapshot.Path, launch.Environment[PackagerChildBridge.RunSnapshotVariable]);
        Assert.Equal(downloadRoot, launch.Environment[PackagerChildBridge.DownloadRootVariable]);
        Assert.Contains("setup.exe /other", launch.Environment[PackagerChildBridge.CommandsVariable]);
        Assert.Throws<InvalidOperationException>(() => PackagerChildBridge.FromSnapshot(_t.Store, new RunSnapshot { ApplicationId = App }, @"C:\dl"));
    }
}

[Collection(WorkbenchEnvironmentCollection.Name)]
public sealed class AppDataPathsTests
{
    [Fact]
    public void TheSettingsRootVariable_MovesSettingsTemplatesAndTheDefaultWorkbenchRoot()
    {
        var previous = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);
        var root = Path.Combine(Path.GetTempPath(), "ap-settings-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, root);

            Assert.Equal(root, AppDataPaths.Root);
            Assert.Equal(Path.Combine(root, "Workbench"), WorkbenchPaths.DefaultDataRoot);
            Assert.Equal(Path.Combine(root, "condition-templates.json"), new ConditionTemplateService().TemplatesPath);

            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, null);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPackager"), AppDataPaths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, previous);
        }
    }
}
