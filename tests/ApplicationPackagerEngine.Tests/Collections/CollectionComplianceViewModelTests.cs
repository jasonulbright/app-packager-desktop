using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>The Collection and Compliance page logic over fake services, without a window.</summary>
public sealed class CollectionComplianceViewModelTests
{
    private static async Task<CollectionComplianceViewModel> SelectAsync(CollectionComplianceViewModel viewModel, string id)
    {
        viewModel.SelectedRow = viewModel.Rows.Single(r => r.CollectionId == id);
        viewModel.SetSelectedRows([viewModel.SelectedRow]);
        await viewModel.DetailLoad;
        return viewModel;
    }

    private static string AllPlanText(ChangePlan plan) => string.Join("\n", plan.Items.Select(i => $"{i.Action}|{i.Target}|{i.Detail}"));

    private static void AssertNoSiteTextInLog(CollectionComplianceHarness harness, params string[] texts)
    {
        foreach (var entry in harness.Log.Entries)
            foreach (var text in texts)
                Assert.DoesNotContain(text, entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheConstructorReadsNothing()
    {
        var harness = new CollectionComplianceHarness();
        _ = harness.Create();
        Assert.Equal(0, harness.Service.ListCalls);
        Assert.Equal(0, harness.SettingsLoads);
        Assert.Equal(0, harness.Store.Reads);
    }

    [Fact]
    public async Task TheFirstVisitListsTheCollectionsWithTheirCountsAndReadsNoMembers()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        Assert.Equal(1, harness.Service.ListCalls);
        Assert.Equal(0, harness.Service.DetailCalls);
        Assert.Equal(0, harness.Service.QueryListCalls);
        Assert.Equal(["All Systems", "Pilot", "Servers", "Workstations"], viewModel.Rows.Select(r => r.Name));
        Assert.Equal("4 collections", viewModel.RowCount);
        var servers = viewModel.Rows.Single(r => r.CollectionId == "LAB00010");
        Assert.Equal("1 query, 1 direct", servers.RulesText);
        Assert.Equal("Built-in", viewModel.Rows.Single(r => r.CollectionId == "SMS00001").TypeText);
        Assert.Equal("Servers (LAB00010)", servers.ToString());
    }

    [Fact]
    public async Task SelectingACollectionLoadsItsDetailAndMembersPageByPage()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00010");
        Assert.Equal(1, harness.Service.DetailCalls);
        Assert.Contains(viewModel.Properties, p => p.Name == "Limiting collection" && p.Value == "All Systems (SMS00001)");
        Assert.Equal(["All servers"], viewModel.QueryRules.Select(r => r.RuleName));
        Assert.Equal(HarnessWql(), viewModel.QueryRules[0].Expression);
        Assert.Equal(2, viewModel.Members.Count);
        Assert.Equal("Members 1 to 2 of 3", viewModel.MemberPageText);
        Assert.True(viewModel.NextMemberPageCommand.CanExecute(null));
        Assert.False(viewModel.PreviousMemberPageCommand.CanExecute(null));
        await viewModel.NextMemberPageCommand.ExecuteAsync(null);
        Assert.Equal([0, 1], harness.Service.DetailPages);
        Assert.Single(viewModel.Members);
        Assert.Equal("Members 3 to 3 of 3", viewModel.MemberPageText);
        Assert.False(viewModel.NextMemberPageCommand.CanExecute(null));

        await SelectAsync(viewModel, "LAB00012");
        Assert.Equal(["Servers (LAB00010)"], viewModel.IncludeRules.Select(r => r.Text));
        Assert.Equal(["Workstations (LAB00011)"], viewModel.ExcludeRules.Select(r => r.Text));
    }

    private static string HarnessWql() => CollectionComplianceHarness.ServersWql;

    [Fact]
    public async Task TheFiltersNarrowTheGridAndTheCountSaysHowMany()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.TypeFilter = CollectionComplianceViewModel.BuiltInFilter;
        Assert.Equal(["All Systems"], viewModel.Rows.Select(r => r.Name));
        viewModel.TypeFilter = CollectionComplianceViewModel.CustomFilter;
        Assert.Equal(3, viewModel.Rows.Count);
        Assert.Equal("3 of 4 collections", viewModel.RowCount);
        viewModel.TypeFilter = "";
        viewModel.OnlyEmpty = true;
        Assert.Equal(["Workstations"], viewModel.Rows.Select(r => r.Name));
        viewModel.OnlyEmpty = false;
        viewModel.OnlyWithDirectMembers = true;
        Assert.Equal(["Servers"], viewModel.Rows.Select(r => r.Name));
        viewModel.OnlyWithDirectMembers = false;
        viewModel.OnlyWithQueryRules = true;
        Assert.Equal(["Servers"], viewModel.Rows.Select(r => r.Name));
        viewModel.OnlyWithQueryRules = false;
        Assert.Contains(viewModel.Folders, f => f.Value == @"Patching\Clients");
        viewModel.FolderFilter = @"Patching\Clients";
        Assert.Equal(["Workstations"], viewModel.Rows.Select(r => r.Name));
        viewModel.FolderFilter = "";
        viewModel.TextFilter = "lab00012";
        Assert.Equal(["Pilot"], viewModel.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task AMissingSiteCodeShowsOneMessageAndReadsNothing()
    {
        var harness = new CollectionComplianceHarness();
        harness.Settings.SiteCode = "";
        var viewModel = await harness.OpenAsync();
        Assert.Equal(CollectionComplianceViewModel.SiteCodeMessage, viewModel.ConnectionMessage);
        Assert.Equal(0, harness.Service.ListCalls);
        harness.Settings.SiteCode = "LAB";
        harness.Settings.Systems = new() { ConfigMgr = false };
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.NotInUseMessage, viewModel.ConnectionMessage);
    }

    [Fact]
    public async Task AFailedReadShowsAPlainMessageAndLogsNoRawText()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.ListFailure = new InvalidOperationException("raw provider text at line 7");
        var viewModel = await harness.OpenAsync();
        Assert.Equal(CollectionComplianceViewModel.LoadFailed, viewModel.ConnectionMessage);
        AssertNoSiteTextInLog(harness, "raw provider text");

        harness.Service.ListFailure = new ConfigMgrSessionException(ConfigMgrFailure.AccessDenied, "Access is denied.");
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Access is denied." + CollectionComplianceViewModel.AccessHint, viewModel.ConnectionMessage);

        harness.Service.ListFailure = null;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("", viewModel.ConnectionMessage);
        Assert.Equal(4, viewModel.Rows.Count);
    }

    [Fact]
    public async Task LeavingThePageDuringALoadDiscardsTheLateResult()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = harness.Create();
        var visit = viewModel.OnNavigatedToAsync();
        while (Volatile.Read(ref harness.Service.ListCalls) == 0) await Task.Delay(5);
        await viewModel.OnNavigatedFromAsync();
        harness.Service.ListGate.SetResult();
        await visit;
        Assert.Empty(viewModel.Rows);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.IsDataLoaded);
    }

    [Fact]
    public async Task LeavingThePageReleasesTheDataAndTheNextVisitReadsAgain()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00010");
        await viewModel.OnNavigatedFromAsync();
        Assert.Empty(viewModel.Rows);
        Assert.Empty(viewModel.Members);
        Assert.Empty(viewModel.QueryRules);
        Assert.Null(viewModel.SelectedRow);
        await viewModel.OnNavigatedToAsync();
        Assert.Equal(2, harness.Service.ListCalls);
    }

    [Fact]
    public async Task ADetailThatArrivesAfterTheSelectionChangedIsDiscarded()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        harness.Service.DetailGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SelectedRow = viewModel.Rows.Single(r => r.CollectionId == "LAB00010");
        var first = viewModel.DetailLoad;
        harness.Service.DetailGate = null;
        viewModel.SelectedRow = viewModel.Rows.Single(r => r.CollectionId == "LAB00012");
        await viewModel.DetailLoad;
        await first;
        Assert.Empty(viewModel.QueryRules);
        Assert.Single(viewModel.IncludeRules);
        Assert.Contains(viewModel.Properties, p => p.Name == "Name" && p.Value == "Pilot");

        harness.Service.DetailGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.SelectedRow = viewModel.Rows.Single(r => r.CollectionId == "LAB00010");
        var pending = viewModel.DetailLoad;
        await viewModel.OnNavigatedFromAsync();
        harness.Service.DetailGate.SetResult();
        await pending;
        Assert.Empty(viewModel.QueryRules);
        Assert.Empty(viewModel.Members);
        Assert.False(viewModel.IsDetailLoading);
    }

    [Fact]
    public async Task OpenByIdReadsOneCollectionWithoutTheList()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.ListGate = new TaskCompletionSource();
        var viewModel = harness.Create();
        viewModel.OpenByIdText = "not an id";
        await viewModel.OpenByIdCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.InvalidCollectionId, viewModel.StatusMessage);
        Assert.Equal(0, harness.Service.GetCalls);

        viewModel.OpenByIdText = "LAB000FF";
        await viewModel.OpenByIdCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.CollectionNotFound("LAB000FF"), viewModel.StatusMessage);

        viewModel.OpenByIdText = " lab00011 ";
        await viewModel.OpenByIdCommand.ExecuteAsync(null);
        await viewModel.DetailLoad;
        Assert.Equal(0, harness.Service.ListCalls);
        Assert.Equal(["Workstations"], viewModel.Rows.Select(r => r.Name));
        Assert.Equal("LAB00011", viewModel.SelectedRow?.CollectionId);
        Assert.Equal(CollectionComplianceViewModel.OpenedById, viewModel.RowCount);
        Assert.Equal(1, harness.Service.DetailCalls);
    }

    [Fact]
    public async Task OpenByIdSelectsACollectionOfALoadedList()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.OpenByIdText = "LAB00012";
        await viewModel.OpenByIdCommand.ExecuteAsync(null);
        Assert.Equal(4, viewModel.Rows.Count);
        Assert.Equal("Pilot", viewModel.SelectedRow?.Name);
    }

    [Fact]
    public async Task ConnectSiteTestsTheConnectionWithOneReadAndNoInventory()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = harness.Create();
        await viewModel.ConnectSiteCommand.ExecuteAsync(null);
        Assert.Equal(1, harness.Service.GetCalls);
        Assert.Equal(0, harness.Service.ListCalls);
        Assert.Equal(CollectionComplianceViewModel.Connected("LAB"), viewModel.StatusMessage);

        harness.Service.GetFailure = new ConfigMgrSessionException(ConfigMgrFailure.InvalidSiteCode, "The site code LAB was not found.");
        await viewModel.ConnectSiteCommand.ExecuteAsync(null);
        Assert.Equal("The site code LAB was not found." + CollectionComplianceViewModel.SiteHint, viewModel.ConnectionMessage);

        harness.Settings.SiteCode = "";
        await viewModel.ConnectSiteCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.SiteCodeMessage, viewModel.ConnectionMessage);
        Assert.Equal(2, harness.Service.GetCalls);
    }

    private static void FillNew(CollectionEditorViewModel editor, string name, string limitingId = "SMS00001")
    {
        editor.Name = name;
        editor.Comment = "Pilot ring";
        editor.RefreshType = "Both";
        editor.SelectedLimiting = editor.LimitingChoices.Single(c => c.Row.CollectionId == limitingId);
    }

    [Fact]
    public async Task NewCollectionWritesOnlyThroughAConfirmedPlanAndOffersUndo()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        harness.Prompts.Editor = e =>
        {
            Assert.Equal(CollectionEditorMode.New, e.Mode);
            Assert.Equal("All Systems (SMS00001)", e.SelectedLimiting?.Text);
            FillNew(e, "CCM-Test-1");
            FakeCollectionPrompts.Save(e);
            Assert.True(e.Saved);
        };
        await viewModel.NewCollectionCommand.ExecuteAsync(null);

        var plan = Assert.Single(harness.Plans.Shown);
        var item = Assert.Single(plan.Items);
        Assert.Equal(("Create", "CCM-Test-1"), (item.Action, item.Target));
        Assert.Contains("Limiting collection: All Systems (SMS00001)", item.Detail);
        Assert.Contains("Refresh: Both", item.Detail);
        Assert.True(plan.HasUndo);
        Assert.Equal(CollectionChangeKind.Create, Assert.Single(Assert.Single(harness.Service.Applied)).Kind);
        Assert.Contains(viewModel.Rows, r => r.Name == "CCM-Test-1");
        Assert.Equal(2, harness.Service.ListCalls);
        Assert.True(viewModel.HasUndo);
        Assert.Equal("New collection: 1 changed, 0 failed.", viewModel.ResultSummary);

        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal("Undo: New collection", harness.Plans.Shown[^1].Title);
        Assert.Contains("Removes CCM-Test-1", harness.Plans.Shown[^1].Items[0].Detail);
        Assert.DoesNotContain(viewModel.Rows, r => r.Name == "CCM-Test-1");
        var entries = harness.History.Entries;
        Assert.Equal(["Create", "Undo Create"], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal("Collection and Compliance", e.Tool));
        AssertNoSiteTextInLog(harness, "CCM-Test-1", "Pilot ring");
    }

    [Fact]
    public async Task ADeclinedPlanChangesNothingAndTheEditorSaysSo()
    {
        var harness = new CollectionComplianceHarness();
        harness.Plans.Confirm = false;
        var viewModel = await harness.OpenAsync();
        CollectionEditorViewModel? shown = null;
        harness.Prompts.Editor = e =>
        {
            shown = e;
            FillNew(e, "CCM-Test-2");
            FakeCollectionPrompts.Save(e);
        };
        await viewModel.NewCollectionCommand.ExecuteAsync(null);
        Assert.Single(harness.Plans.Shown);
        Assert.Empty(harness.Service.Applied);
        Assert.False(shown!.Saved);
        Assert.Equal(CollectionComplianceViewModel.NotConfirmed, shown.ValidationMessage);
        Assert.Equal(1, harness.Service.ListCalls);
    }

    [Fact]
    public async Task TheEditorRefusesAMissingOrTakenNameAndAMissingOrSelfLimitingCollection()
    {
        var harness = new CollectionComplianceHarness();
        var collections = harness.Service.Collections;
        var editor = new CollectionEditorViewModel(CollectionEditorMode.New, collections, _ => Task.FromResult(true));
        editor.Name = "  ";
        Assert.Equal(CollectionEditorViewModel.NameRequired, editor.Validate());
        editor.Name = "servers";
        Assert.Equal(CollectionEditorViewModel.NameTaken("servers"), editor.Validate());
        editor.Name = "New one";
        editor.SelectedLimiting = null;
        Assert.Equal(CollectionEditorViewModel.LimitingRequired, editor.Validate());
        editor.Name = "Bad\u0007name";
        Assert.Equal(CollectionEditorViewModel.NameInvalid, editor.Validate());

        var edit = new CollectionEditorViewModel(CollectionEditorMode.Edit, collections, _ => Task.FromResult(true), harness.Servers);
        Assert.Equal("Servers", edit.Name);
        Assert.Null(edit.Validate());
        Assert.DoesNotContain(edit.LimitingChoices, c => c.Row.CollectionId == "LAB00010");
        edit.SelectedLimiting = new CollectionChoiceRow(harness.Servers);
        Assert.Equal(CollectionEditorViewModel.LimitingSelf, edit.Validate());

        editor.Name = "";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.False(editor.Saved);
        Assert.Equal(CollectionEditorViewModel.NameRequired, editor.ValidationMessage);
    }

    [Fact]
    public async Task TheLimitingListFiltersAndOffersNoTargetThatWouldLimitItself()
    {
        var harness = new CollectionComplianceHarness();
        var editor = new CollectionEditorViewModel(CollectionEditorMode.Edit, harness.Service.Collections, _ => Task.FromResult(true), harness.Workstations);
        Assert.DoesNotContain(editor.LimitingChoices, c => c.Row.CollectionId == "LAB00011");
        Assert.DoesNotContain(editor.LimitingChoices, c => c.Row.CollectionId == "LAB00012");
        editor.LimitingFilter = "serv";
        Assert.Equal(["Servers (LAB00010)"], editor.LimitingChoices.Select(c => c.Text));
        Assert.Equal("All Systems (SMS00001)", editor.SelectedLimiting?.Text);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EditShowsOldAndNewValuesAndUndoSetsThemBack()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00011");
        harness.Prompts.Editor = e =>
        {
            Assert.Equal(CollectionEditorMode.Edit, e.Mode);
            e.Comment = "All clients";
            e.RefreshType = "Manual";
            FakeCollectionPrompts.Save(e);
        };
        await viewModel.EditCollectionCommand.ExecuteAsync(null);
        var item = Assert.Single(harness.Plans.Shown[0].Items);
        Assert.Equal("Set properties", item.Action);
        Assert.Contains("Refresh: Periodic to Manual", item.Detail);
        Assert.Contains("Comment: (none) to All clients", item.Detail);
        Assert.DoesNotContain("Name:", item.Detail);
        Assert.Equal("Manual", harness.Service.Collections.Single(c => c.CollectionId == "LAB00011").RefreshType);

        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal("Periodic", harness.Service.Collections.Single(c => c.CollectionId == "LAB00011").RefreshType);
    }

    [Fact]
    public async Task ABuiltInCollectionCannotBeEditedOrRemoved()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "SMS00001");
        Assert.False(viewModel.EditCollectionCommand.CanExecute(null));
        Assert.False(viewModel.RemoveCollectionCommand.CanExecute(null));
        Assert.Equal(CollectionComplianceViewModel.BuiltInRefused, viewModel.BuiltInTooltip);
        await viewModel.RemoveCollectionCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.True(viewModel.CopyCollectionCommand.CanExecute(null));
        Assert.True(viewModel.EvaluateCommand.CanExecute(null));
    }

    [Fact]
    public async Task CopyCreatesTheCopyThroughAPlanAndUndoRemovesIt()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00010");
        harness.Prompts.Editor = e =>
        {
            Assert.Equal(CollectionEditorMode.Copy, e.Mode);
            Assert.Equal("Copy of Servers", e.Name);
            Assert.False(e.CanEditProperties);
            e.Name = "Servers 2";
            FakeCollectionPrompts.Save(e);
        };
        await viewModel.CopyCollectionCommand.ExecuteAsync(null);
        var item = Assert.Single(harness.Plans.Shown[0].Items);
        Assert.Equal(("Copy", "Servers (LAB00010)"), (item.Action, item.Target));
        Assert.Contains("New name: Servers 2", item.Detail);
        Assert.Contains(viewModel.Rows, r => r.Name == "Servers 2");
        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.DoesNotContain(viewModel.Rows, r => r.Name == "Servers 2");
    }

    [Fact]
    public async Task EvaluateListsTheCollectionsAndHasNoUndo()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.SetSelectedRows(viewModel.Rows.Where(r => r.CollectionId is "LAB00010" or "LAB00011"));
        await viewModel.EvaluateCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.False(plan.HasUndo);
        Assert.Equal("Update membership of 2 collections", plan.ActionName);
        Assert.All(plan.Items, i => Assert.Contains(CollectionComplianceViewModel.EvaluateNote, i.Detail));
        Assert.All(plan.Items, i => Assert.Contains(CollectionComplianceViewModel.NoUndoNote, i.Detail));
        Assert.False(viewModel.HasUndo);
        Assert.Equal("Update membership: 2 changed, 0 failed.", viewModel.ResultSummary);
    }

    [Fact]
    public async Task RemoveShowsTheImpactLinesIsDestructiveAndSaysWhatUndoRestores()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00012");
        await viewModel.RemoveCollectionCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.True(plan.IsDestructive);
        var item = Assert.Single(plan.Items);
        Assert.Equal(("Remove", "Pilot (LAB00012)"), (item.Action, item.Target));
        Assert.Contains("1 deployment is removed with the collection.", item.Detail);
        Assert.Contains(CollectionWriter.RecreatedNote, item.Detail);
        Assert.DoesNotContain(viewModel.Rows, r => r.CollectionId == "LAB00012");

        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Contains(viewModel.Rows, r => r.Name == "Pilot");
        Assert.Contains(viewModel.LastResults, r => r.Message.Contains(CollectionWriter.RecreatedNote, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovingALimitingCollectionIsRefusedWithThePlainMessage()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00011");
        await viewModel.RemoveCollectionCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Equal("Workstations is the limiting collection of another collection and cannot be removed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AStateChangeBetweenTheReadAndThePlanShowsThePlainMessageAndWritesNothing()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00010");
        harness.Service.PrepareFails = _ => new CollectionServiceException("The collection Servers no longer exists. Refresh the list.");
        await viewModel.EvaluateCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Equal("The collection Servers no longer exists. Refresh the list.", viewModel.StatusMessage);
        harness.Service.PrepareFails = _ => new InvalidOperationException("raw text");
        await viewModel.EvaluateCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.PrepareFailed, viewModel.StatusMessage);
        Assert.Empty(harness.Service.Applied);
    }

    [Fact]
    public async Task DirectMembersAreFoundByNameAndEachIsAPlanLine()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00011");
        harness.Prompts.Prompt = p =>
        {
            p.Text = "PC01\r\nPC02\r\nNOPE";
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.AddDirectMembersCommand.ExecuteAsync(null);
        var lookup = Assert.Single(harness.Service.PreviewExpressions);
        Assert.Contains("SMS_R_System.Name = 'PC01'", lookup);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal(["Skip|NOPE|" + CollectionComplianceViewModel.DeviceNotFound,
            "Add|Workstations (LAB00011)|Direct member PC01 (16777201).", "Add|Workstations (LAB00011)|Direct member PC02 (16777202)."], plan.Items.Select(i => $"{i.Action}|{i.Target}|{i.Detail}"));
        Assert.Equal(2, harness.Service.Rules["LAB00011"].Count(r => r.Kind == CollectionRuleKind.Direct));
        AssertNoSiteTextInLog(harness, "PC01");
    }

    [Fact]
    public async Task ADeviceNameWithAQuoteIsQuotedInTheLookup()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.Devices.Add(("16777300", "O'Brien-PC"));
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00011");
        harness.Prompts.Prompt = p =>
        {
            p.Text = "O'Brien-PC";
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.AddDirectMembersCommand.ExecuteAsync(null);
        Assert.Contains(@"SMS_R_System.Name = 'O\'Brien-PC'", Assert.Single(harness.Service.PreviewExpressions));
        Assert.Single(harness.Plans.Shown);
    }

    [Fact]
    public async Task RemovingDirectMembersIsDestructiveAndUndoAddsThemBack()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00010");
        viewModel.SetSelectedMembers(viewModel.Members.Where(m => m.IsDirect));
        Assert.True(viewModel.RemoveDirectMembersCommand.CanExecute(null));
        await viewModel.RemoveDirectMembersCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.True(plan.IsDestructive);
        Assert.Equal("Remove", plan.Items[0].Action);
        Assert.DoesNotContain(harness.Service.Rules["LAB00010"], r => r.Kind == CollectionRuleKind.Direct);
        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Contains(harness.Service.Rules["LAB00010"], r => r.Kind == CollectionRuleKind.Direct && r.ResourceId == "16777001");
    }

    [Fact]
    public async Task IncludeAndExcludeRulesNameBothCollectionsAndSayTheyChangeMembership()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00011");
        harness.Prompts.Picker = p =>
        {
            Assert.DoesNotContain(p.Items, i => i.Row.CollectionId == "LAB00011");
            p.Items.Single(i => i.Row.CollectionId == "LAB00010").IsChecked = true;
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.AddIncludeRuleCommand.ExecuteAsync(null);
        var item = Assert.Single(harness.Plans.Shown[^1].Items);
        Assert.Equal(("Add", "Workstations (LAB00011)"), (item.Action, item.Target));
        Assert.Contains("Include rule: Servers (LAB00010)", item.Detail);
        Assert.Contains(CollectionComplianceViewModel.MembershipNote, item.Detail);

        await viewModel.AddExcludeRuleCommand.ExecuteAsync(null);
        Assert.Contains("Exclude rule: Servers (LAB00010)", harness.Plans.Shown[^1].Items[0].Detail);
        Assert.Equal(2, harness.Service.Rules["LAB00011"].Count);

        await viewModel.DetailLoad;
        viewModel.SelectedIncludeRule = viewModel.IncludeRules.Single();
        await viewModel.RemoveIncludeRuleCommand.ExecuteAsync(null);
        Assert.True(harness.Plans.Shown[^1].IsDestructive);
        Assert.Contains(CollectionComplianceViewModel.MembershipNote, harness.Plans.Shown[^1].Items[0].Detail);
        Assert.DoesNotContain(harness.Service.Rules["LAB00011"], r => r.Kind == CollectionRuleKind.Include);
    }

    private static async Task<CollectionComplianceViewModel> WqlOnAsync(CollectionComplianceHarness harness, string id)
    {
        var viewModel = await harness.OpenAsync();
        viewModel.WqlCollection = viewModel.Rows.Single(r => r.CollectionId == id);
        await viewModel.WqlRulesLoad;
        return viewModel;
    }

    [Fact]
    public async Task TheWqlTreeGroupsTheCollectionsByFolderAndFilters()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        Assert.Equal(["Patching", "All Systems (SMS00001)", "Pilot (LAB00012)"], viewModel.WqlTree.Select(n => n.Text));
        var patching = viewModel.WqlTree[0];
        Assert.Equal(["Clients", "Servers"], patching.Children.Select(n => n.Text));
        Assert.Equal("Servers (LAB00010)", patching.Children[1].Children.Single().Text);
        viewModel.WqlFilter = "work";
        Assert.Equal("Workstations (LAB00011)", viewModel.WqlTree.Single().Children.Single().Children.Single().Text);
        Assert.True(viewModel.WqlTree.Single().IsExpanded);
    }

    [Fact]
    public async Task ValidateAndPreviewShowTheResultAndPreviewCanBeCancelled()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await WqlOnAsync(harness, "LAB00011");
        viewModel.WqlText = "delete from SMS_R_System";
        await viewModel.ValidateWqlCommand.ExecuteAsync(null);
        Assert.Equal("The query is not a valid WQL select statement.", viewModel.WqlMessage);
        viewModel.WqlText = "select SMS_R_System.ResourceId, SMS_R_System.Name from SMS_R_System";
        await viewModel.ValidateWqlCommand.ExecuteAsync(null);
        Assert.Equal("The query is valid.", viewModel.WqlMessage);

        await viewModel.PreviewWqlCommand.ExecuteAsync(null);
        Assert.Equal(["SMS_R_System.ResourceId", "SMS_R_System.Name"], viewModel.PreviewColumns);
        Assert.Equal(2, viewModel.PreviewRows.Count);
        Assert.Equal("PC01", viewModel.PreviewRows[0][1]);
        Assert.Equal("16777201, PC01", viewModel.PreviewRows[0].ToString());
        Assert.Equal(CollectionComplianceViewModel.PreviewText(2, 2), viewModel.WqlMessage);

        harness.Service.PreviewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = viewModel.PreviewWqlCommand.ExecuteAsync(null);
        while (!viewModel.IsPreviewing) await Task.Delay(5);
        Assert.True(viewModel.CancelPreviewCommand.CanExecute(null));
        viewModel.CancelPreviewCommand.Execute(null);
        await preview;
        Assert.Equal(CollectionComplianceViewModel.PreviewCancelled, viewModel.WqlMessage);
        Assert.False(viewModel.IsPreviewing);
        Assert.Empty(viewModel.PreviewRows);
        AssertNoSiteTextInLog(harness, "SMS_R_System");
    }

    [Fact]
    public async Task AddRuleShowsTheWqlAndTheCountOfTheLastPreviewOfThatWql()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await WqlOnAsync(harness, "LAB00011");
        const string wql = "select SMS_R_System.ResourceId, SMS_R_System.Name from SMS_R_System";
        viewModel.WqlText = wql;
        viewModel.WqlRuleName = "All devices";
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        var item = Assert.Single(harness.Plans.Shown[^1].Items);
        Assert.Equal(("Add", "Workstations (LAB00011)"), (item.Action, item.Target));
        Assert.Contains("Query rule All devices: " + wql, item.Detail);
        Assert.Contains(CollectionComplianceViewModel.NoPreviewNote, item.Detail);
        Assert.Equal(0, harness.Service.PreviewCalls);

        await viewModel.PreviewWqlCommand.ExecuteAsync(null);
        viewModel.WqlRuleName = "All devices 2";
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        Assert.Contains(CollectionComplianceViewModel.PreviewNote(2), harness.Plans.Shown[^1].Items[0].Detail);
        Assert.Equal(1, harness.Service.PreviewCalls);
        Assert.Equal(["All devices", "All devices 2"], viewModel.WqlRules.Select(r => r.RuleName));
    }

    [Fact]
    public async Task AddRuleNeedsACollectionANameThatIsNotTakenAndAQuery()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.WqlText = "select * from SMS_R_System";
        viewModel.WqlRuleName = "X";
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.PickCollection, viewModel.WqlMessage);
        viewModel.WqlCollection = viewModel.Rows.Single(r => r.CollectionId == "LAB00010");
        await viewModel.WqlRulesLoad;
        viewModel.WqlRuleName = "all servers";
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.RuleNameTaken("all servers"), viewModel.WqlMessage);
        viewModel.WqlRuleName = "";
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.RuleNameRequired, viewModel.WqlMessage);
        viewModel.WqlRuleName = "New";
        viewModel.WqlText = "  ";
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.QueryRequired, viewModel.WqlMessage);
        viewModel.WqlCollection = viewModel.Rows.Single(r => r.CollectionId == "SMS00001");
        Assert.Equal(CollectionComplianceViewModel.BuiltInRefused, viewModel.WqlCollectionNote);
        Assert.Empty(harness.Plans.Shown);
    }

    [Fact]
    public async Task UpdateRuleStatesTheSafeOrderAndUndoRestoresTheOldWql()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await WqlOnAsync(harness, "LAB00010");
        viewModel.SelectedWqlRule = viewModel.WqlRules.Single();
        Assert.Equal(CollectionComplianceHarness.ServersWql, viewModel.WqlText);
        Assert.Equal("All servers", viewModel.WqlRuleName);
        const string updated = "select SMS_R_System.ResourceId from SMS_R_System where SMS_R_System.Name like 'SRV%'";
        viewModel.WqlText = updated;
        await viewModel.UpdateRuleCommand.ExecuteAsync(null);
        var item = Assert.Single(harness.Plans.Shown[^1].Items);
        Assert.Equal("Update", item.Action);
        Assert.Contains("Old: " + CollectionComplianceHarness.ServersWql, item.Detail);
        Assert.Contains("New: " + updated, item.Detail);
        Assert.Contains(CollectionComplianceViewModel.UpdateOrderNote, item.Detail);
        Assert.Equal(updated, harness.Service.Rules["LAB00010"].Single(r => r.Kind == CollectionRuleKind.Query).Expression);

        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceHarness.ServersWql, harness.Service.Rules["LAB00010"].Single(r => r.Kind == CollectionRuleKind.Query).Expression);
    }

    [Fact]
    public async Task RemoveRuleIsDestructiveAndShowsTheWql()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await WqlOnAsync(harness, "LAB00010");
        viewModel.SelectedWqlRule = viewModel.WqlRules.Single();
        await viewModel.RemoveRuleCommand.ExecuteAsync(null);
        var plan = harness.Plans.Shown[^1];
        Assert.True(plan.IsDestructive);
        Assert.Contains(CollectionComplianceHarness.ServersWql, plan.Items[0].Detail);
        Assert.Empty(viewModel.WqlRules);
    }

    [Fact]
    public async Task EditInTheWqlEditorOpensTheRuleOfTheDetailPanel()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00010");
        viewModel.SelectedQueryRule = viewModel.QueryRules.Single();
        await viewModel.EditRuleInEditorCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.WqlTab, viewModel.SelectedTabIndex);
        Assert.Equal("LAB00010", viewModel.WqlCollection?.CollectionId);
        Assert.Equal("All servers", viewModel.SelectedWqlRule?.RuleName);
        Assert.Equal(CollectionComplianceHarness.ServersWql, viewModel.WqlText);
    }

    [Fact]
    public void TemplateValuesAreLiteralsWithBackslashEscapesOrWholeNumbersAndControlCharactersAreRefused()
    {
        Assert.Equal("where Build = 22631", WqlParameters.Insert("where Build = {Build}", [("{Build}", "22631")]));
        Assert.Throws<ArgumentException>(() => WqlParameters.Insert("where Build = {Build}", [("{Build}", "10.0")]));
        Assert.Equal(@"like '%O\'Brien%'", WqlParameters.Insert("like '%{Name}%'", [("{Name}", "O'Brien")]));
        Assert.Equal("like \"%a\\\"b%\"", WqlParameters.Insert("like \"%{Name}%\"", [("{Name}", "a\"b")]));
        Assert.Equal(@"= 'LAB\\Group'", WqlParameters.Insert("= '{G}'", [("{G}", @"LAB\Group")]));
        Assert.Equal(@"= '\' or 1=1 or \''", WqlParameters.Insert("= '{G}'", [("{G}", "' or 1=1 or '")]));
        Assert.Equal(@"= '\\\' or 1=1 --'", WqlParameters.Insert("= '{G}'", [("{G}", @"\' or 1=1 --")]));
        Assert.Equal(WqlParameters.ControlCharacter, WqlParameters.Problem("a\nb"));
        Assert.Equal(WqlParameters.ControlCharacter, WqlParameters.Problem("a\u0000b"));
        Assert.Equal(WqlParameters.TooLong, WqlParameters.Problem(new string('x', 257)));
        Assert.Equal(WqlParameters.Required, WqlParameters.Problem(" "));
        Assert.Null(WqlParameters.Problem("7-Zip"));
        Assert.Equal("Build", WqlParameters.Label("{Build}"));
        Assert.Equal("{Build}", WqlParameters.Placeholder("Build"));
    }

    [Fact]
    public async Task ParameterizedTemplatesShowTheFinalWqlBeforeUse()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        Assert.Equal(3, viewModel.TemplateRows.Count);
        viewModel.CategoryFilter = "Software";
        var row = Assert.Single(viewModel.TemplateRows);
        Assert.Equal("User", row.Source);
        viewModel.SelectedTemplate = row;
        var parameter = Assert.Single(viewModel.TemplateParameters);
        Assert.Equal("Software name", parameter.Label);
        Assert.Equal("Part of the display name, for example 7-Zip.", parameter.HelpText);
        Assert.Equal("", viewModel.TemplateWql);
        Assert.Equal(WqlParameters.Required, viewModel.TemplateMessage);
        Assert.False(viewModel.CopyTemplateToEditorCommand.CanExecute(null));
        parameter.Value = "O'Brien";
        Assert.EndsWith(@"like '%O\'Brien%'", viewModel.TemplateWql);
        Assert.Equal("", viewModel.TemplateMessage);
        parameter.Value = "bad\tvalue";
        Assert.Equal("", viewModel.TemplateWql);
        Assert.Equal(WqlParameters.ControlCharacter, viewModel.TemplateMessage);
        parameter.Value = "7-Zip";
        await viewModel.CopyTemplateToEditorCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.WqlTab, viewModel.SelectedTabIndex);
        Assert.EndsWith("like '%7-Zip%'", viewModel.WqlText);
        Assert.Equal("Software installed by name", viewModel.WqlRuleName);
    }

    [Fact]
    public async Task ApplyTemplateSkipsACollectionWithThatRuleNameAndUndoRemovesOnlyTheAddedRules()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.AddRule("LAB00012", FakeCollectionService.QueryRule("All workstations", "select 1"));
        var viewModel = await harness.OpenAsync();
        viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "All workstations");
        harness.Prompts.Picker = p =>
        {
            Assert.DoesNotContain(p.Items, i => i.Row.IsBuiltIn);
            foreach (var i in p.Items) i.IsChecked = true;
            FakeCollectionPrompts.Accept(p);
            Assert.True(p.Accepted);
        };
        await viewModel.ApplyTemplateCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal("Add 2 rules", plan.ActionName);
        Assert.Equal(["Skip|Pilot (LAB00012)", "Add|Servers (LAB00010)", "Add|Workstations (LAB00011)"], plan.Items.Select(i => $"{i.Action}|{i.Target}"));
        Assert.Contains(CollectionComplianceViewModel.RuleNameExists("All workstations"), plan.Items[0].Detail);
        Assert.Equal("Apply template All workstations: 2 changed, 0 failed, 1 skipped.", viewModel.ResultSummary);

        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal(2, harness.Plans.Shown[^1].Items.Count);
        Assert.DoesNotContain(harness.Service.Rules["LAB00010"], r => r.RuleName == "All workstations");
        Assert.Contains(harness.Service.Rules["LAB00012"], r => r.RuleName == "All workstations");
    }

    [Fact]
    public async Task ACancelDuringABulkApplyKeepsThePartialResultsAndTheirUndo()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "All workstations");
        harness.Service.AfterChange = i =>
        {
            if (i == 0) harness.Plans.LastViewModel!.CancelCommand.Execute(null);
        };
        harness.Prompts.Picker = p =>
        {
            foreach (var i in p.Items) i.IsChecked = true;
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.ApplyTemplateCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.LastResults.Count(r => r.Succeeded));
        Assert.Equal(2, viewModel.LastResults.Count(r => r.Skipped));
        Assert.True(viewModel.HasUndo);
        Assert.Equal(SiteOperationOutcome.Cancelled, harness.History.Entries.Single().Outcome);
    }

    [Fact]
    public async Task ApplyTemplateNeedsTheParametersFirst()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "OS build");
        Assert.False(viewModel.ApplyTemplateCommand.CanExecute(null));
        viewModel.TemplateParameters[0].Value = "22631";
        Assert.True(viewModel.ApplyTemplateCommand.CanExecute(null));
        Assert.EndsWith("SMS_R_System.Build = 22631", viewModel.TemplateWql);
    }

    [Fact]
    public async Task ASkippedUserTemplateFileShowsThePlainMessageOfTheStore()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        Assert.False(viewModel.HasTemplatesMessage);
        harness.Store.Message = @"1 user template file was skipped because it cannot be read: broken.json.";
        var next = await harness.OpenAsync();
        Assert.Equal(harness.Store.Message, next.TemplatesMessage);
        Assert.True(next.HasTemplatesMessage);
        Assert.Equal(3, next.TemplateRows.Count);
    }

    [Fact]
    public async Task ATemplateParameterStartsWithItsDefaultValue()
    {
        var harness = new CollectionComplianceHarness();
        harness.Store.Templates.Add(new QueryTemplate("os", "OS version", "Operating system", "select * from SMS_R_System where Version = '{V}'", ["{V}"], "", true,
            [new QueryTemplateParameter("{V}", "Version", "", "10.0")]));
        var viewModel = await harness.OpenAsync();
        viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "OS version");
        Assert.Equal("10.0", viewModel.TemplateParameters.Single().Value);
        Assert.Equal("select * from SMS_R_System where Version = '10.0'", viewModel.TemplateWql);
        Assert.Equal("Shipped", viewModel.SelectedTemplate.Source);
    }

    [Fact]
    public async Task SavedQueriesLoadWhenTheTabOpensAndEveryWriteHasAPlanAndUndo()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        Assert.Equal(0, harness.Service.QueryListCalls);
        viewModel.SelectedTabIndex = CollectionComplianceViewModel.SiteQueriesTab;
        await viewModel.QueriesLoad;
        Assert.Equal(1, harness.Service.QueryListCalls);
        Assert.Equal(["All clients", "Old OS"], viewModel.SavedQueries.Select(q => q.Name));

        viewModel.SelectedSavedQuery = viewModel.SavedQueries[1];
        await viewModel.LoadQueryToEditorCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.WqlTab, viewModel.SelectedTabIndex);
        Assert.Equal("select * from SMS_R_System where Build < '10.0'", viewModel.WqlText);

        viewModel.WqlText = "select * from SMS_R_System where Client = 0";
        harness.Prompts.Prompt = p =>
        {
            Assert.Equal(TextPromptViewModel.Required, p.Validate());
            p.Text = "No client";
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.SaveQueryAsNewCommand.ExecuteAsync(null);
        Assert.Contains("select * from SMS_R_System where Client = 0", harness.Plans.Shown[^1].Items[0].Detail);
        Assert.Contains(harness.Service.Queries, q => q.Name == "No client");
        Assert.Contains(viewModel.SavedQueries, q => q.Name == "No client");

        viewModel.SelectedSavedQuery = viewModel.SavedQueries.Single(q => q.Name == "Old OS");
        viewModel.WqlText = "select * from SMS_R_System where Build < '6.3'";
        await viewModel.UpdateSavedQueryCommand.ExecuteAsync(null);
        var update = harness.Plans.Shown[^1].Items[0].Detail!;
        Assert.Contains("Old: select * from SMS_R_System where Build < '10.0'", update);
        Assert.Contains("New: select * from SMS_R_System where Build < '6.3'", update);
        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal("select * from SMS_R_System where Build < '10.0'", harness.Service.Queries.Single(q => q.Name == "Old OS").Expression);

        viewModel.SelectedSavedQuery = viewModel.SavedQueries.Single(q => q.Name == "All clients");
        await viewModel.DeleteSavedQueryCommand.ExecuteAsync(null);
        Assert.True(harness.Plans.Shown[^1].IsDestructive);
        Assert.Contains(CollectionWriter.NewQueryIdNote, harness.Plans.Shown[^1].Items[0].Detail);
        Assert.DoesNotContain(harness.Service.Queries, q => q.Name == "All clients");
        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Contains(harness.Service.Queries, q => q.Name == "All clients");
        AssertNoSiteTextInLog(harness, "No client", "Client = 0");
    }

    [Fact]
    public async Task ADoubleClickOnSaveShowsOnePlan()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        harness.Prompts.Editor = e =>
        {
            FillNew(e, "CCM-Test-3");
            var first = e.SaveCommand.ExecuteAsync(null);
            var second = e.SaveCommand.ExecuteAsync(null);
            ScriptedPlanDialogs.Wait(Task.WhenAll(first, second));
        };
        await viewModel.NewCollectionCommand.ExecuteAsync(null);
        Assert.Single(harness.Plans.Shown);
        Assert.Single(harness.Service.Applied);
    }

    [Fact]
    public async Task AWriteWaitsForARunningLoadAndThenReadsAgain()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00011");
        harness.Service.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = viewModel.RefreshCommand.ExecuteAsync(null);
        harness.Prompts.Editor = e =>
        {
            e.Comment = "changed";
            FakeCollectionPrompts.Save(e);
        };
        var edit = viewModel.EditCollectionCommand.ExecuteAsync(null);
        await Task.Delay(50);
        harness.Service.ListGate.SetResult();
        await Task.WhenAll(refresh, edit);
        Assert.Equal(3, harness.Service.ListCalls);
        Assert.Equal("changed", viewModel.Rows.Single(r => r.CollectionId == "LAB00011").Row.Comment);
    }

    [Fact]
    public async Task ExportWritesTheShownRowsAndOpensTheFolder()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.TypeFilter = CollectionComplianceViewModel.CustomFilter;
        await viewModel.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(3, harness.Reports.Written.Single().Count);
        Assert.Equal([@"C:\data\Collections\Reports"], harness.Prompts.OpenedFolders);
        harness.Reports.Failure = new IOException("disk text");
        await viewModel.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.ReportFailed("HTML"), viewModel.StatusMessage);
    }

    [Fact]
    public async Task ComplianceBaselinesStartsTheSuiteToolAndShowsItsMessage()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = harness.Create();
        await viewModel.OpenComplianceBaselinesCommand.ExecuteAsync(null);
        Assert.Equal(1, harness.Launches);
        Assert.Equal(CollectionComplianceViewModel.ComplianceStarted, viewModel.StatusMessage);
        harness.LaunchMessage = "Collection and Compliance needs AppPackager Suite, which is not installed.";
        await viewModel.OpenComplianceBaselinesCommand.ExecuteAsync(null);
        Assert.Equal(harness.LaunchMessage, viewModel.StatusMessage);
        Assert.Equal(0, harness.Service.ListCalls);
    }

    [Fact]
    public async Task TheTabAndFiltersComeBackOnTheNextStartAndOlderFilesUseTheDefaults()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        Assert.Equal(0, viewModel.SelectedTabIndex);
        viewModel.SelectedTabIndex = CollectionComplianceViewModel.TemplatesTab;
        viewModel.DetailTabIndex = 2;
        viewModel.TypeFilter = CollectionComplianceViewModel.CustomFilter;
        viewModel.OnlyWithQueryRules = true;
        viewModel.FolderFilter = @"Patching\Servers";
        viewModel.TextFilter = "serv";
        viewModel.CategoryFilter = "Software";
        await viewModel.OnNavigatedFromAsync();
        Assert.NotNull(harness.Saved);

        var next = harness.Create();
        await next.OnNavigatedToAsync();
        Assert.Equal(CollectionComplianceViewModel.TemplatesTab, next.SelectedTabIndex);
        Assert.Equal(2, next.DetailTabIndex);
        Assert.Equal(CollectionComplianceViewModel.CustomFilter, next.TypeFilter);
        Assert.True(next.OnlyWithQueryRules);
        Assert.Equal(@"Patching\Servers", next.FolderFilter);
        Assert.Equal("serv", next.TextFilter);
        Assert.Equal("Software", next.CategoryFilter);
        Assert.Equal(["Servers"], next.Rows.Select(r => r.Name));

        harness.Saved = new CollectionComplianceSettings { SelectedTab = 99, DetailTab = -1, TypeFilter = "Bogus", TextFilter = null!, FolderFilter = null!, TemplateCategory = null! };
        var odd = await harness.OpenAsync();
        Assert.Equal((0, 0, "", "", ""), (odd.SelectedTabIndex, odd.DetailTabIndex, odd.TypeFilter, odd.TextFilter, odd.FolderFilter));
        Assert.Equal(CollectionComplianceViewModel.AllCategories, odd.CategoryFilter);
    }

    [Fact]
    public async Task LeavingThePageWaitsForTheSaveOfTheChoices()
    {
        var harness = new CollectionComplianceHarness();
        harness.BeforeSave = _ => Thread.Sleep(100);
        var viewModel = await harness.OpenAsync();
        viewModel.OnlyEmpty = true;
        viewModel.TextFilter = "x";
        await viewModel.OnNavigatedFromAsync();
        Assert.True(harness.Saved!.OnlyEmpty);
        Assert.Equal("x", harness.Saved.TextFilter);
    }

    [Fact]
    public void AnOlderSettingsFileHasNoCollectionComplianceObject()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"SiteCode\":\"LAB\"}")!;
        Assert.Null(settings.CollectionCompliance);
        var text = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("CollectionCompliance", text);
    }

    [Fact]
    public async Task AFailedWriteKeepsTheResultsAndTheUndoOfTheChangesThatWereMade()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "All workstations");
        harness.Service.Fail = c => c.CollectionId == "LAB00011" ? "Access is denied for Workstations." : null;
        harness.Prompts.Picker = p =>
        {
            foreach (var i in p.Items) i.IsChecked = true;
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.ApplyTemplateCommand.ExecuteAsync(null);
        Assert.Equal("Apply template All workstations: 2 changed, 1 failed.", viewModel.ResultSummary);
        Assert.Contains(viewModel.LastResults, r => r.Message == "Access is denied for Workstations.");
        Assert.True(viewModel.HasUndo);
        Assert.Equal(SiteOperationOutcome.PartlySucceeded, harness.History.Entries.Single().Outcome);
    }
}
