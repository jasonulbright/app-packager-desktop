using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class DeploymentHistoryOptionsTests
{
    private readonly DeploymentPageHarness _h = new();

    private async Task<DeploymentHistoryViewModel> HistoryAsync()
    {
        _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00010", "{0A1B2C3D-0000-4000-8000-000000000001}", when: new DateTime(2026, 10, 19, 8, 0, 0)));
        _h.Audit.Records.Add(_h.Record("Distribute", DeploymentObjectType.Application, "7-Zip", "LAB00010", "", when: new DateTime(2026, 10, 19, 7, 0, 0)));
        _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Package, "Office Tools", "LAB00011", "", "Failed", new DateTime(2026, 10, 18, 7, 0, 0)));
        _h.Audit.Records.Add(_h.Record("Undo", DeploymentObjectType.Application, "=cmd|' /C calc'!A0", "LAB00011", "{DEP-0009}", when: new DateTime(2026, 10, 17, 7, 0, 0)));
        _h.Saved = new ApplicationPackagerUI.Models.DeploymentHelperSettings { SelectedTab = DeploymentHelperViewModel.HistoryTab };
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        return page.History;
    }

    [Fact]
    public async Task TheHistoryListsTheAuditLogNewestFirstAndTheFiltersNarrowIt()
    {
        var history = await HistoryAsync();
        Assert.Equal(4, history.Rows.Count);
        Assert.Equal("7-Zip 1.0", history.Rows[0].Object);
        Assert.Equal(@"C:\Data\Deployments\deployment-audit.jsonl", history.LogPath);
        Assert.Equal(0, _h.Service.ListCalls);

        history.EventFilter = "Create";
        Assert.Equal(2, history.Rows.Count);
        history.TypeFilter = "Package";
        Assert.Equal(["Office Tools 1.0"], history.Rows.Select(r => r.Object));
        Assert.Equal("1 of 4 records", history.RowCount);
        history.EventFilter = DeploymentHistoryViewModel.AllText;
        history.TypeFilter = DeploymentHistoryViewModel.AllText;
        history.TextFilter = "DEP-0009";
        Assert.Equal("Undo", history.Rows.Single().Event);
    }

    [Fact]
    public async Task RemoveDeploymentOfACreateRecordOpensTheRemovePlanAndTheServiceDecides()
    {
        var history = await HistoryAsync();
        _h.Service.Deployments.Add(new DeploymentRecord(DeploymentId: "{0A1B2C3D-0000-4000-8000-000000000001}", Type: DeploymentObjectType.Application, ObjectId: "16777300", ObjectName: "7-Zip",
            CollectionId: "LAB00010", CollectionName: "Pilot Workstations", Purpose: "Available", AvailableTime: new DateTime(2026, 10, 19, 8, 0, 0), Deadline: null,
            Created: new DateTime(2026, 10, 19, 8, 0, 0), CreatedBy: @"LAB\admin"));
        history.SelectedRow = history.Rows.Single(r => r.DeploymentId == "{0A1B2C3D-0000-4000-8000-000000000001}");
        await history.RemoveDeploymentCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        Assert.True(plan.IsDestructive);
        Assert.False(plan.HasUndo);
        Assert.Contains("Deployment ID: {0A1B2C3D-0000-4000-8000-000000000001}.", plan.Items[0].Detail);
        Assert.Contains("this product created it", plan.Items[0].Detail);
        var change = _h.Service.Applied.Single().Single();
        Assert.Equal(DeploymentChangeKind.RemoveDeployment, change.Kind);
        Assert.Equal("{0A1B2C3D-0000-4000-8000-000000000001}", change.Existing!.DeploymentId);
        Assert.Equal(DeploymentObjectType.Application, change.Existing.Type);
        Assert.Equal("LAB00010", change.Existing.CollectionId);
        Assert.Empty(_h.Service.Deployments);
    }

    [Fact]
    public async Task RemoveDeploymentIsRefusedForARecordWithoutIdAndForADeploymentThatIsGone()
    {
        var history = await HistoryAsync();
        history.SelectedRow = history.Rows.Single(r => r.Object == "Office Tools 1.0");
        await history.RemoveDeploymentCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentHistoryViewModel.NoDeploymentId, history.Shared.StatusMessage);
        history.SelectedRow = history.Rows.Single(r => r.Event == "Undo");
        await history.RemoveDeploymentCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentHistoryViewModel.NoDeploymentId, history.Shared.StatusMessage);

        history.SelectedRow = history.Rows.Single(r => r.DeploymentId == "{0A1B2C3D-0000-4000-8000-000000000001}");
        await history.RemoveDeploymentCommand.ExecuteAsync(null);
        Assert.Equal("The deployment {0A1B2C3D-0000-4000-8000-000000000001} no longer exists. Nothing was changed.", history.Shared.StatusMessage);
        Assert.Empty(_h.Plans.Shown);
        Assert.Empty(_h.Service.Applied);
    }

    [Fact]
    public async Task ExportWritesTheShownRowsAndOpenFolderOpensTheReportFolder()
    {
        var history = await HistoryAsync();
        Assert.False(history.OpenReportsFolderCommand.CanExecute(null));
        history.EventFilter = "Create";
        await history.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(2, _h.Reports.Written.Single().Count);
        Assert.Equal(@"Saved the CSV report to C:\Data\Deployments\Reports\deployments.csv: 2 records, the rows the filters show.", history.Shared.StatusMessage);
        history.OpenReportsFolderCommand.Execute(null);
        Assert.Equal([@"C:\Data\Deployments\Reports"], _h.Dialogs.OpenedFolders);

        history.TextFilter = "nothing matches this";
        await history.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentHistoryViewModel.NothingToExport, history.Shared.StatusMessage);

        history.TextFilter = "";
        _h.Reports.Failure = new IOException(@"Access to C:\secret denied");
        await history.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentHistoryViewModel.ReportFailed("HTML"), history.Shared.StatusMessage);
    }

    [Fact]
    public async Task AnExportAfterAFailedReadIsRefused()
    {
        _h.Audit.ReadFailure = new IOException("locked");
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        Assert.Equal(DeploymentHistoryViewModel.ReadFailedText, page.Shared.StatusMessage);
        await page.History.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentHistoryViewModel.NotLoaded, page.Shared.StatusMessage);
        Assert.Empty(_h.Reports.Written);
    }

    [Fact]
    public async Task ACreateAddsToTheHistoryWhenTheHistoryWasRead()
    {
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        Assert.Empty(page.History.Rows);
        page.SelectedTabIndex = DeploymentHelperViewModel.DeployTab;
        await page.Deploy.EnsureLoadedAsync();
        await DeploymentPageHarness.PickAsync(page.Deploy, _h.SevenZip, "LAB00010");
        _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00010", "{DEP-0100}"));
        await page.Deploy.DeployCommand.ExecuteAsync(null);
        Assert.Single(page.History.Rows);
    }

    [Fact]
    public async Task TheOptionsShowTheProbeTheTemplatesAndTheConnection()
    {
        _h.Audit.NextProbe = new AuditProbe(false, "Another program holds the file.");
        _h.Saved = new ApplicationPackagerUI.Models.DeploymentHelperSettings { SelectedTab = DeploymentHelperViewModel.OptionsTab };
        var page = await _h.OpenAsync(DeploymentHelperViewModel.OptionsTab);
        var options = page.Options;
        Assert.False(options.ProbeWritable);
        Assert.Equal("The audit log cannot be written, so no deployment can be created. Another program holds the file.", options.ProbeText);
        Assert.Equal(3, options.Templates.Count);
        Assert.Equal("Site code LAB, SMS Provider cm01.lab.local. Change them in Settings, ConfigMgr Site Settings.", options.ConnectionText);
        Assert.Equal(0, _h.Service.ListCalls);
    }

    [Theory]
    [InlineData("relative\\audit.jsonl")]
    [InlineData("C:\\Logs\\")]
    [InlineData("audit.jsonl")]
    public async Task ARelativeOrFolderPathIsRefusedAndNotSaved(string path)
    {
        _h.Saved = new ApplicationPackagerUI.Models.DeploymentHelperSettings { SelectedTab = DeploymentHelperViewModel.OptionsTab };
        var page = await _h.OpenAsync(DeploymentHelperViewModel.OptionsTab);
        page.Options.AuditLogPath = path;
        await page.Options.SavePathCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentOptionsViewModel.PathNotRooted, page.Options.ProbeText);
        Assert.Equal(0, _h.PreferenceSaves);
    }

    [Theory]
    [InlineData(@"D:\Logs\deployment-audit.jsonl")]
    [InlineData(@"\\fs01\deploy$\deployment-audit.jsonl")]
    [InlineData("")]
    public async Task AFullOrSharePathIsSavedAndThenProbed(string path)
    {
        _h.Saved = new ApplicationPackagerUI.Models.DeploymentHelperSettings { AuditLogPath = @"E:\old.jsonl", SelectedTab = DeploymentHelperViewModel.OptionsTab };
        var page = await _h.OpenAsync(DeploymentHelperViewModel.OptionsTab);
        Assert.Equal(@"E:\old.jsonl", page.Options.AuditLogPath);
        var probes = _h.Audit.Probes;
        _h.BeforeSave = s => Assert.Equal(probes, _h.Audit.Probes);
        page.Options.AuditLogPath = path;
        await page.Options.SavePathCommand.ExecuteAsync(null);
        Assert.Equal(path, _h.Saved!.AuditLogPath);
        Assert.Equal(probes + 1, _h.Audit.Probes);
        Assert.True(page.Options.ProbeWritable);
    }

    [Fact]
    public async Task ShippedTemplatesCannotBeEditedOrDeletedAndAUserTemplateCan()
    {
        var page = await _h.OpenAsync(DeploymentHelperViewModel.OptionsTab);
        var options = page.Options;
        options.SelectedTemplate = options.Templates.Single(t => t.Name == "Workstation Pilot");
        options.EditTemplateCommand.Execute(null);
        await options.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentOptionsViewModel.ShippedReadOnly, options.TemplateMessage);
        Assert.Equal(0, _h.Dialogs.Editors);
        Assert.Equal(3, _h.Templates.Templates.Count);

        options.SelectedTemplate = options.Templates.Single(t => t.Name == "Lab Nightly");
        _h.Dialogs.ConfirmDelete = false;
        await options.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.Equal(3, _h.Templates.Templates.Count);
        _h.Dialogs.ConfirmDelete = true;
        await options.DeleteTemplateCommand.ExecuteAsync(null);
        Assert.DoesNotContain(_h.Templates.Templates, t => t.Id == "lab-nightly");
        Assert.Equal(2, options.Templates.Count);
    }

    [Fact]
    public async Task NewAndEditSaveAUserTemplateAndRenameReplacesTheOldFile()
    {
        var page = await _h.OpenAsync(DeploymentHelperViewModel.OptionsTab);
        var options = page.Options;
        DeploymentTemplateEditorViewModel? editor = null;
        _h.Dialogs.Editor = e => editor = e;
        options.NewTemplateCommand.Execute(null);
        editor!.Name = "Workstation Production";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(ApplicationPackagerUI.ViewModels.Pages.DeploymentFormViewModel.TemplateNameTaken, editor.ValidationMessage);
        editor.Name = "Kiosk";
        editor.IsRequired = true;
        editor.DeadlineOffsetText = "abc";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(DeploymentTemplateEditorViewModel.OffsetInvalid, editor.ValidationMessage);
        editor.DeadlineOffsetText = "12";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.Saved);
        Assert.Equal(12, _h.Templates.Templates.Single(t => t.Id == "kiosk").DeadlineOffsetHours);

        options.SelectedTemplate = options.Templates.Single(t => t.Name == "Kiosk");
        options.EditTemplateCommand.Execute(null);
        Assert.Equal("Kiosk", editor.Name);
        Assert.True(editor.IsRequired);
        editor.Name = "Kiosk Night";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains(_h.Templates.Templates, t => t.Id == "kiosk-night");
        Assert.DoesNotContain(_h.Templates.Templates, t => t.Id == "kiosk");
    }

    [Fact]
    public async Task TheTemplateEditorRefusesAnAvailableTemplateThatHidesEverything()
    {
        var editor = new DeploymentTemplateEditorViewModel(null, _h.Templates.All(), (_, _) => Task.FromResult(true))
        {
            Name = "Silent",
            Notification = DeploymentText.Notifications.Single(n => n.Value == DeploymentText.HideAll)
        };
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.False(editor.Saved);
        Assert.StartsWith("An Available template cannot hide all notifications", editor.ValidationMessage);
        await Task.CompletedTask;
    }
}
