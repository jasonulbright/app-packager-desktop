using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Deployments.Rings;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// The plan editor keeps every ring option, a refresh keeps the run choices, late and stale run reads,
/// the folder change, the plan start basis, the picker double-click, the editor close, the plan texts,
/// and the ring writer over the real ring and deployment services with the stub site.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DeploymentRingsFixTests
{
    private static Task WaitAsync(Func<bool> condition) => DeploymentPageHarness.WaitAsync(condition);

    private static CollectionRow Row(string id, string name, string folder = "Rings", bool builtIn = false) => new(CollectionId: id, Name: name, FolderPath: folder,
        LimitingCollectionId: "SMS00001", LimitingCollectionName: "All Systems", MemberCount: 10, IsBuiltIn: builtIn, RefreshType: "Periodic", Comment: "",
        DirectRuleCount: 0, QueryRuleCount: 0, IncludeRuleCount: 0, ExcludeRuleCount: 0);

    /// <summary>Every field of the plan file set to a value other than its default, on an Available and a Required ring.</summary>
    private static RingPlan EveryField() => new(Name: "Kiosk-Rings", Description: "Kiosks first, then the shops.", HoldLaterRings: true, TimeBasedOn: "Utc",
        Rings:
        [
            new RingDefinition(Name: "Kiosks", CollectionId: "LAB00010", CollectionName: "Pilot Workstations", Purpose: "Available", AvailableOffsetDays: 1, DeadlineOffsetDays: null,
                UserNotification: "DisplaySoftwareCenterOnly", OverrideServiceWindow: true, RebootOutsideServiceWindow: true, AllowMetered: true, DpGroup: "Branch DPs",
                SuccessThresholdPercent: 95, FastNetworkOption: "RunProgramFromDistributionPoint", SlowNetworkOption: "DownloadContentFromDistributionPointAndLocally",
                RerunBehavior: "AlwaysRerunProgram", AllowBoundaryFallback: false, AllowMicrosoftUpdate: true, RequirePostRebootFullScan: false, ShowTaskSequenceProgress: false,
                TaskSequenceAvailability: "MediaAndPxe", TaskSequenceDeploymentOption: "DownloadContentLocallyWhenNeededByRunningTaskSequence"),
            new RingDefinition(Name: "Shops", CollectionId: "LAB00011", CollectionName: "Server Group A", Purpose: "Required", AvailableOffsetDays: 3, DeadlineOffsetDays: 5,
                UserNotification: "HideAll", OverrideServiceWindow: true, RebootOutsideServiceWindow: false, AllowMetered: true, DpGroup: "All DPs",
                SuccessThresholdPercent: 80, FastNetworkOption: "RunProgramFromDistributionPoint", SlowNetworkOption: "DownloadContentFromDistributionPointAndLocally",
                RerunBehavior: "RerunIfFailedPreviousAttempt", AllowBoundaryFallback: false, AllowMicrosoftUpdate: true, RequirePostRebootFullScan: false,
                ShowTaskSequenceProgress: false, TaskSequenceAvailability: "Clients", TaskSequenceDeploymentOption: "DownloadAllContentLocallyBeforeStartingTaskSequence")
        ], IsSeed: false, FileName: "Kiosk-Rings.json");

    private static RingPlanEditorViewModel Editor(RingPlan? plan, bool copy = false, IReadOnlyList<RingPlan>? existing = null, Func<IReadOnlyList<CollectionRow>>? pickMany = null,
        Func<bool>? confirmDiscard = null) =>
        new(plan, copy, existing ?? (plan is null ? [] : [plan]), [Row("LAB00010", "Pilot Workstations"), Row("LAB00011", "Server Group A")], (_, _) => Task.FromResult(true),
            _ => null, pickMany, confirmDiscard);

    // ---- P1: the editor keeps every ring option ------------------------------------------------------

    [Fact]
    public void EditingAPlanWithEveryFieldSetSavesItUnchanged()
    {
        var plan = EveryField();
        var built = Editor(plan).Build(out var problem);
        Assert.Null(problem);
        Assert.Equal(plan.Rings, built!.Rings);
        Assert.Equal((plan.Name, plan.Description, plan.HoldLaterRings, plan.TimeBasedOn), (built.Name, built.Description, built.HoldLaterRings, built.TimeBasedOn));
    }

    [Fact]
    public void RenamingARingChangesItsNameOnlyAndATaskSequenceRingStaysMediaAndPxe()
    {
        var plan = EveryField();
        var editor = Editor(plan);
        editor.Rings[0].Name = "Kiosks first";
        var built = editor.Build(out _)!;
        Assert.Equal(plan.Rings[0] with { Name = "Kiosks first" }, built.Rings[0]);
        Assert.Equal("MediaAndPxe", built.Rings[0].TaskSequenceAvailability);
        Assert.Equal(plan.Rings[1], built.Rings[1]);
    }

    [Fact]
    public void ACopyAndADuplicateKeepEveryOption()
    {
        var plan = EveryField();
        var copy = Editor(plan, copy: true);
        Assert.Equal(plan.Rings, copy.Build(out _)!.Rings);

        copy.SelectedRing = copy.Rings[1];
        copy.DuplicateRingCommand.Execute(null);
        Assert.Equal(plan.Rings[1] with { Name = "Shops copy", CollectionId = "", CollectionName = "" }, copy.Rings[2].ToDefinition());
    }

    [Fact]
    public void FillOffsetsAndApplyToAllChangeOnlyWhatTheyName()
    {
        var plan = EveryField();
        var editor = Editor(plan);
        editor.FirstOffsetText = "2";
        editor.DaysBetweenText = "4";
        editor.DeadlineDaysText = "3";
        editor.FillOffsetsCommand.Execute(null);
        Assert.Equal(plan.Rings[0] with { AvailableOffsetDays = 2 }, editor.Rings[0].ToDefinition());
        Assert.Equal(plan.Rings[1] with { AvailableOffsetDays = 6, DeadlineOffsetDays = 9 }, editor.Rings[1].ToDefinition());

        editor.SelectedRing = editor.Rings[0];
        editor.ApplyPurpose = editor.ApplyOptions = false;
        editor.ApplyNotification = true;
        editor.Rings[1].IsRequired = false;
        editor.ApplyToAllCommand.Execute(null);
        Assert.Equal("DisplaySoftwareCenterOnly", editor.Rings[1].ToDefinition().UserNotification);
        Assert.Equal(("Clients", false, "RerunIfFailedPreviousAttempt"),
            (editor.Rings[1].ToDefinition().TaskSequenceAvailability, editor.Rings[1].ToDefinition().AllowBoundaryFallback, editor.Rings[1].ToDefinition().RerunBehavior));

        editor.ApplyNotification = false;
        editor.ApplyOptions = true;
        editor.ApplyToAllCommand.Execute(null);
        var second = editor.Rings[1].ToDefinition();
        Assert.Equal(("MediaAndPxe", "DownloadContentLocallyWhenNeededByRunningTaskSequence", "AlwaysRerunProgram", true),
            (second.TaskSequenceAvailability, second.TaskSequenceDeploymentOption, second.RerunBehavior, second.RebootOutsideServiceWindow));
    }

    [Fact]
    public void ApplyToAllSetsAnOptionBackToItsDefaultWhenTheSourceHasTheDefault()
    {
        var plan = EveryField();
        var editor = Editor(plan);
        editor.Rings[0].FastNetwork = RingPlanEditorViewModel.FastNetworkChoices[0];
        editor.Rings[0].TaskSequenceContent = RingPlanEditorViewModel.TaskSequenceContentChoices[0];
        editor.SelectedRing = editor.Rings[0];
        editor.ApplyPurpose = editor.ApplyNotification = false;
        editor.ApplyToAllCommand.Execute(null);
        Assert.All(editor.Rings, r => Assert.Equal((null, null), (r.ToDefinition().FastNetworkOption, r.ToDefinition().TaskSequenceDeploymentOption)));
    }

    [Fact]
    public async Task SavingThroughThePageWritesTheRingsWithEveryOption()
    {
        var h = new DeploymentPageHarness();
        var plan = EveryField();
        h.Rings.Plans.Add(plan);
        var page = await h.OpenAsync(DeploymentHelperViewModel.RingsTab);
        RingPlanEditorViewModel? editor = null;
        h.Dialogs.RingEditor = e => editor = e;
        page.Rings.SelectedPlan = page.Rings.Plans.Single(p => p.Name == plan.Name);
        page.Rings.EditPlanCommand.Execute(null);
        editor!.Rings[1].Name = "Shops and outlets";
        await editor.SaveCommand.ExecuteAsync(null);
        var saved = h.Rings.Saved.Single().Plan;
        Assert.Equal([plan.Rings[0], plan.Rings[1] with { Name = "Shops and outlets" }], saved.Rings);
    }

    // ---- P2: a refresh keeps the run choices ---------------------------------------------------------

    [Fact]
    public async Task ARefreshKeepsTheHoldChoiceTheExpansionAndThePlanRow()
    {
        var h = new DeploymentPageHarness();
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        rings.SelectedObject = rings.Objects.Single(o => o.Object.Id == h.SevenZip.Id);
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Pilot-Rings");
        rings.HoldLaterRings = true;
        await rings.ExpandCommand.ExecuteAsync(null);
        var row = rings.SelectedPlan;

        await rings.RefreshCommand.ExecuteAsync(null);
        Assert.Same(row, rings.SelectedPlan);
        Assert.True(rings.HoldLaterRings);
        Assert.Equal(3, rings.PreviewRows.Count);

        lock (h.Rings.Plans) h.Rings.Plans[1] = h.PilotRings with { Description = "Changed in another session." };
        await rings.RefreshCommand.ExecuteAsync(null);
        Assert.NotSame(row, rings.SelectedPlan);
        Assert.True(rings.HoldLaterRings);
        Assert.Empty(rings.PreviewRows);
        Assert.Equal(DeploymentRingsViewModel.ExpandAgain, rings.ValidationMessage);
    }

    // ---- P3 and P4: run reads ------------------------------------------------------------------------

    [Fact]
    public async Task ATabLoadThatEndsAfterANewerRunsReadDoesNotShowItsOlderRuns()
    {
        var h = new DeploymentPageHarness();
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Rings.StaleRunGates.Enqueue(gate);
        var calls = h.Rings.ListRunCalls;
        var refresh = rings.RefreshCommand.ExecuteAsync(null);
        await WaitAsync(() => h.Rings.ListRunCalls > calls);
        h.Rings.AddRun("RUN-NEW", h.SevenZip, ("Pilot", "LAB00010", "Created", null, "Available"));
        await rings.LoadRunsAsync();
        Assert.Equal("RUN-NEW", rings.Runs.Single().RunId);
        gate.SetResult();
        await refresh;
        Assert.Equal("RUN-NEW", rings.Runs.Single().RunId);
        Assert.False(rings.IsLoadingRuns);
    }

    [Fact]
    public async Task TheRunsMessageIsClearedByTheNextReadAndByAnotherRun()
    {
        var h = new DeploymentPageHarness();
        h.Rings.AddRun("RUN-001", h.SevenZip, ("Pilot", "LAB00010", "Created", null, "Available"), ("Broad", "LAB00012", "Held", null, "Available"));
        h.Rings.AddRun("RUN-002", h.Notepad, ("Pilot", "LAB00010", "Created", null, "Available"), ("Broad", "LAB00012", "Held", null, "Available"));
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        rings.SelectedRun = rings.Runs.Single(r => r.RunId == "RUN-001");
        await rings.CountsTask;
        h.Rings.LockOwner = @"LAB\other on PC2";
        await rings.CloseRunCommand.ExecuteAsync(null);
        Assert.Contains("locked by another session", rings.RunsMessage);

        rings.SelectedRun = rings.Runs.Single(r => r.RunId == "RUN-002");
        Assert.Equal("", rings.RunsMessage);

        await rings.CloseRunCommand.ExecuteAsync(null);
        Assert.NotEqual("", rings.RunsMessage);
        await rings.LoadRunsAsync();
        Assert.Equal("", rings.RunsMessage);
    }

    // ---- P5: the ring folders ------------------------------------------------------------------------

    [Fact]
    public async Task SavingTheRingFoldersReleasesTheRingsTabAndIsRefusedWhileAChangeRuns()
    {
        var h = new DeploymentPageHarness();
        var page = await h.OpenAsync(DeploymentHelperViewModel.RingsTab);
        Assert.True(page.Rings.IsLoaded);
        page.SelectedTabIndex = DeploymentHelperViewModel.OptionsTab;
        await page.Options.EnsureLoadedAsync();
        var folders = page.Options.RingFolders!;

        page.Shared.IsWriting = true;
        folders.RingsFolder = @"\\fs01\deploy$\Rings";
        await folders.SaveFoldersCommand.ExecuteAsync(null);
        Assert.Equal(RingFolderOptionsViewModel.WriteRunningText, folders.ProbeText);
        Assert.True(page.Rings.IsLoaded);
        Assert.NotEqual(@"\\fs01\deploy$\Rings", h.Saved?.RingsFolder);

        page.Shared.IsWriting = false;
        await folders.SaveFoldersCommand.ExecuteAsync(null);
        Assert.Equal(@"\\fs01\deploy$\Rings", h.Saved!.RingsFolder);
        Assert.False(page.Rings.IsLoaded);
        Assert.Empty(page.Rings.Plans);

        var lists = h.Rings.ListPlanCalls;
        page.SelectedTabIndex = DeploymentHelperViewModel.RingsTab;
        await WaitAsync(() => page.Rings.IsLoaded);
        Assert.Equal(lists + 1, h.Rings.ListPlanCalls);
    }

    // ---- P6: the plan start follows the plan's time basis --------------------------------------------

    [Fact]
    public async Task TheDefaultPlanStartUsesTheClockOfThePlanUntilTheOperatorSetsIt()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus2", TimeSpan.FromHours(2), "Plus2", "Plus2");
        var h = new DeploymentPageHarness(zone);
        h.Rings.Plans.Add(h.PilotRings with { Name = "Utc-Rings", TimeBasedOn = "Utc" });
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Pilot-Rings");
        Assert.Equal("11:05", rings.PlanStartTimeText);
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Utc-Rings");
        Assert.Equal("09:05", rings.PlanStartTimeText);

        rings.PlanStartTimeText = "14:30";
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Pilot-Rings");
        Assert.Equal("14:30", rings.PlanStartTimeText);
    }

    // ---- P7: the picker double-click -----------------------------------------------------------------

    private static readonly PropertyInfo ClickCount = typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!;

    /// <summary>
    /// Sends a real double-click press to <paramref name="target"/>: the tunneling and the bubbling press with a click count of 2, as the
    /// mouse device raises them; each element on the route raises its left-button press from them, and each control turns that into its
    /// own MouseDoubleClick.
    /// </summary>
    private static void DoubleClick(UIElement target)
    {
        foreach (var routed in new[] { Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent })
        {
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed, Source = target };
            ClickCount.SetValue(press, 2);
            target.RaiseEvent(press);
        }
    }

    private static T Descendant<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && (match?.Invoke(found) ?? true)) return found;
            if (TryDescendant(child, match) is { } deeper) return deeper;
        }
        throw new InvalidOperationException($"No {typeof(T).Name} was found.");
    }

    private static T? TryDescendant<T>(DependencyObject root, Func<T, bool>? match) where T : DependencyObject
    {
        try { return Descendant(root, match); }
        catch (InvalidOperationException) { return null; }
    }

    [Fact]
    public void ADoubleClickPicksOnlyOnARowOfTheSinglePicker()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var collections = new[] { Row("LAB00010", "Pilot Workstations"), Row("LAB00011", "Server Group A") };
            var picker = new RingCollectionPickerViewModel("Pick", collections, "LAB00011");
            var window = new RingCollectionPickerWindow(picker);
            Show(window);
            try
            {
                Settle(window);
                var grid = ById<DataGrid>(window, "dgPickerCollections");
                DoubleClick(Descendant<DataGridColumnHeader>(grid, header => header.Content is "Name"));
                Assert.Null(picker.Result);
                DoubleClick(Descendant<ScrollViewer>(grid));
                Assert.Null(picker.Result);
                var row = Descendant<DataGridRow>(grid, r => r.Item is RingCollectionChoice { CollectionId: "LAB00011" });
                object? seen = null;
                grid.MouseDoubleClick += (_, e) => seen = e.OriginalSource;
                var text = Descendant<TextBlock>(row, t => t.Text == "Server Group A");
                DoubleClick(text);
                Assert.True(seen is not null, "no double click reached the grid");
                Assert.True(RingCollectionPickerWindow.PicksOnDoubleClick(false, seen as DependencyObject), $"rule refused {seen}");
                Assert.True(picker.PickCommand.CanExecute(null), $"cannot pick, selected {picker.SelectedCollection}");
                Assert.Equal("LAB00011", picker.Result?.CollectionId);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ADoubleClickInTheMultiSelectPickerPicksNothing()
    {
        WithApplication(ApplicationTheme.Dark, () =>
        {
            var collections = new[] { Row("LAB00010", "Pilot Workstations"), Row("LAB00011", "Server Group A") };
            var picker = new RingCollectionPickerViewModel("Check", collections, null, multiSelect: true);
            var window = new RingCollectionPickerWindow(picker);
            Show(window);
            try
            {
                Settle(window);
                var grid = ById<DataGrid>(window, "dgPickerCollections");
                var row = Descendant<DataGridRow>(grid);
                ((RingCollectionChoice)row.Item).IsChecked = true;
                DoubleClick(Descendant<CheckBox>(row));
                DoubleClick(Descendant<TextBlock>(row, t => t.Text.Length > 0));
                Assert.Empty(picker.Results);
                Assert.True(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheDoubleClickRuleNamesWhatPicks()
    {
        Assert.False(RingCollectionPickerWindow.PicksOnDoubleClick(false, null));
        Assert.False(RingCollectionPickerWindow.PicksOnDoubleClick(true, null));
    }

    // ---- P8: the old plan cannot be removed ----------------------------------------------------------

    [Fact]
    public async Task ASavedPlanWhoseOldNameCannotBeRemovedIsReportedAsSaved()
    {
        var h = new DeploymentPageHarness();
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Pilot-Rings");
        RingPlanEditorViewModel? editor = null;
        h.Dialogs.RingEditor = e => editor = e;
        rings.EditPlanCommand.Execute(null);
        editor!.Name = "Pilot-Rings 2026";
        h.Rings.DeleteFailure = new IOException("The file is in use.");
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.Saved);
        Assert.Equal("The plan was saved as Pilot-Rings 2026; the old plan Pilot-Rings could not be removed. Delete it in the list when its file can be changed.", rings.PlansMessage);
        Assert.Contains(rings.Plans, p => p.Name == "Pilot-Rings");
        Assert.Equal("Pilot-Rings 2026", rings.SelectedPlan!.Name);
    }

    // ---- P9 and P10: rings from collections ----------------------------------------------------------

    [Fact]
    public void NumbersInNamesSortAsNumbers()
    {
        string[] names = ["Zone 10", "zone 2", "Zone 1", "Zone 02b", "Alpha"];
        Assert.Equal(["Alpha", "Zone 1", "zone 2", "Zone 02b", "Zone 10"], names.Order(NaturalNameComparer.Instance));
    }

    [Fact]
    public void TheSortBoxSortsByNameAndTheClearedBoxKeepsTheOrderThePickerShows()
    {
        IReadOnlyList<CollectionRow> chosen = [Row("LABZ0010", "Zone 10", @"Deploy\A"), Row("LABZ0002", "Zone 2", @"Deploy\B"), Row("LABZ0001", "Zone 1", @"Deploy\C")];
        var editor = Editor(null, pickMany: () => chosen);
        editor.Rings.Clear();
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(["Zone 10", "Zone 2", "Zone 1"], editor.Rings.Select(r => r.Name));

        editor.Rings.Clear();
        editor.SortNewRingsByName = true;
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(["Zone 1", "Zone 2", "Zone 10"], editor.Rings.Select(r => r.Name));

        var picker = new RingCollectionPickerViewModel("Check", chosen, null, multiSelect: true);
        Assert.Equal(["Zone 10", "Zone 2", "Zone 1"], picker.Collections.Select(c => c.Name));
        picker.CheckShownCommand.Execute(null);
        picker.ShownOrder = () => picker.Collections.OrderByDescending(c => c.CollectionId).ToList();
        picker.PickCommand.Execute(null);
        Assert.Equal(["LABZ0010", "LABZ0002", "LABZ0001"], picker.Results.Select(c => c.CollectionId));
    }

    [Fact]
    public void ALongCollectionNameMakesARingNameOfAtMostSixtyFourCharacters()
    {
        var name = new string('N', 70);
        IReadOnlyList<CollectionRow> chosen = [Row("LABN0001", name)];
        var editor = Editor(null, pickMany: () => chosen);
        editor.Name = "Long";
        editor.Rings.Clear();
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(new string('N', 64), editor.Rings.Single().Name);
        Assert.NotNull(editor.Build(out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void AHundredRingsSixtyDaysApartAreRefusedWithThePlainMessageOfTheBuilder()
    {
        IReadOnlyList<CollectionRow> chosen = [.. Enumerable.Range(1, 100).Select(i => Row("LABH" + i.ToString("0000", CultureInfo.InvariantCulture), $"Wave {i}"))];
        var editor = Editor(null, pickMany: () => chosen);
        editor.Rings.Clear();
        editor.DaysBetweenText = "60";
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Empty(editor.Rings);
        Assert.StartsWith("The last ring would be ", editor.ValidationMessage);
        Assert.Contains("at most 3650 days", editor.ValidationMessage);
    }

    // ---- P11: closing the editor ---------------------------------------------------------------------

    [Fact]
    public void TheEditorClosesWithoutAQuestionUntilSomethingChangesAndAfterASave()
    {
        var asked = 0;
        var answer = false;
        var editor = Editor(EveryField(), confirmDiscard: () => { asked++; return answer; });
        Assert.False(editor.HasChanges);
        Assert.True(editor.ConfirmClose());
        Assert.Equal(0, asked);

        editor.Rings[0].AllowMicrosoftUpdate = false;
        Assert.True(editor.HasChanges);
        Assert.False(editor.ConfirmClose());
        Assert.Equal(1, asked);
        editor.Rings[0].AllowMicrosoftUpdate = true;
        Assert.False(editor.HasChanges);

        editor.Description = "Changed.";
        answer = true;
        Assert.True(editor.ConfirmClose());
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task ASavedEditorClosesWithoutTheQuestion()
    {
        var h = new DeploymentPageHarness();
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == "Pilot-Rings");
        RingPlanEditorViewModel? editor = null;
        h.Dialogs.RingEditor = e => editor = e;
        rings.EditPlanCommand.Execute(null);
        editor!.Description = "Three rings over two weeks.";
        h.Dialogs.ConfirmDiscard = false;
        Assert.False(editor.ConfirmClose());
        Assert.Equal(1, h.Dialogs.DiscardPrompts);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.ConfirmClose());
        Assert.Equal(1, h.Dialogs.DiscardPrompts);
    }

    [Fact]
    public void TheEditorWindowStaysOpenOnCancelEscapeOrCloseWhenTheOperatorKeepsTheChanges()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var keep = true;
            var editor = Editor(EveryField(), confirmDiscard: () => !keep);
            var window = new RingPlanEditorWindow(editor);
            Show(window);
            try
            {
                editor.Name = "Kiosk-Rings 2";
                editor.CancelCommand.Execute(null);
                Settle(window);
                Assert.True(window.IsVisible);
                window.Close();
                Settle(window);
                Assert.True(window.IsVisible);
                keep = false;
                window.Close();
                Settle(window);
                Assert.False(window.IsVisible);
            }
            finally
            {
                keep = false;
                if (window.IsVisible) window.Close();
            }
        });
    }

    // ---- P12, C1 and C3: the plan texts --------------------------------------------------------------

    [Fact]
    public void TheSecondBoxNeverHasAnEmptyText()
    {
        Assert.Equal($"I understand that {RingText.BackdatedSentence}.", RingText.SecondConfirmation(0, 0, null, [], "local time", anyBackdated: true));
        Assert.Equal("", RingText.SecondConfirmation(0, 0, null, [], "local time"));
    }

    private static async Task<(DeploymentRingsViewModel Rings, DeploymentPageHarness H)> ExpandedAsync(RingPlan plan, DeployableObject? obj = null, bool hold = false,
        DeploymentPageHarness? harness = null)
    {
        var h = harness ?? new DeploymentPageHarness();
        h.Rings.Plans.Add(plan);
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        var target = obj ?? h.SevenZip;
        if (rings.Type != target.Type)
        {
            rings.TypeIndex = (int)target.Type;
            await rings.ReloadObjectsAsync();
        }
        rings.SelectedObject = rings.Objects.Single(o => o.Object.Id == target.Id);
        if (target.Type == DeploymentObjectType.Package) rings.SelectedProgram = "Install";
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == plan.Name);
        rings.HoldLaterRings = hold;
        await rings.ExpandCommand.ExecuteAsync(null);
        return (rings, h);
    }

    [Fact]
    public async Task ABackdatedRingTheRowsDoNotNameStillGetsTheSecondBox()
    {
        var h = new DeploymentPageHarness();
        h.Rings.AfterPrepare = p => p with { RingChanges = [.. p.RingChanges.Select(c => c with { Collection = c.Collection! with { CollectionId = "LAB0OTHR" } } with { NeedsBackdatedAcknowledgement = true })] };
        var (rings, _) = await ExpandedAsync(new RingPlan("Avail-Rings", "", false, "LocalTime", [FakeRingService.Ring("Pilot", "LAB00010", "Pilot Workstations")], false), harness: h);
        var prepared = await h.Rings.PrepareAsync(rings.BuildRunRequest()!, CancellationToken.None);
        var (plan, _) = RingText.BuildCreateRunPlan(prepared, h.Clock);
        Assert.Equal($"I understand that {RingText.BackdatedSentence}.", plan!.Plan.ExtraConfirmation);
    }

    [Fact]
    public async Task TheCreateRunPlanShowsTheSettingsOfEachRingAlsoOfAHeldRing()
    {
        var plan = EveryField() with { Rings = [EveryField().Rings[0] with { DpGroup = "" }, EveryField().Rings[1] with { DpGroup = "" }] };
        var (rings, h) = await ExpandedAsync(plan, hold: true);
        var prepared = await h.Rings.PrepareAsync(rings.BuildRunRequest()!, CancellationToken.None);
        var (created, problem) = RingText.BuildCreateRunPlan(prepared, h.Clock);
        Assert.Null(problem);
        var first = created!.Plan.Items[0].Detail!;
        Assert.Contains("User notification: Display in Software Center and show all notifications.", first);
        Assert.Contains("Options: ", first);
        var held = created.Plan.Items[1].Detail!;
        Assert.Contains("User notification: Hide in Software Center and all notifications.", held);
        Assert.Contains("The installation runs outside maintenance windows.", held);
        Assert.Contains("Restarts wait for a maintenance window.", held);
        Assert.Contains($"{DeploymentText.MeteredLabel}: yes", held);
        Assert.Null(created.Plan.OptionalChoice);
    }

    [Fact]
    public async Task AHeldTaskSequenceRingShowsItsAvailabilityContentAndProgress()
    {
        var h = new DeploymentPageHarness();
        h.Service.Programs[h.Win11.Id] = [];
        var (rings, _) = await ExpandedAsync(EveryField(), h.Win11, hold: true, harness: h);
        var prepared = await h.Rings.PrepareAsync(rings.BuildRunRequest()!, CancellationToken.None);
        var (plan, _) = RingText.BuildCreateRunPlan(prepared, h.Clock);
        var held = plan!.Plan.Items[1].Detail!;
        Assert.Contains("Available to: Configuration Manager clients only", held);
        Assert.Contains("Content: Download all content locally before the task sequence starts", held);
        Assert.Contains($"{DeploymentText.ShowProgressLabel}: no", held);
        Assert.Contains("Distribution point group: All DPs.", held);
    }

    [Fact]
    public async Task CreateNowIsOfferedOnlyWhenARingCreatedNowDistributesAndTravelsAsTheRunFlag()
    {
        var (rings, h) = await ExpandedAsync(EveryField(), hold: true);
        var prepared = await h.Rings.PrepareAsync(rings.BuildRunRequest()!, CancellationToken.None);
        var (plan, _) = RingText.BuildCreateRunPlan(prepared, h.Clock);
        Assert.Equal(DeploymentText.CreateNowChoice, plan!.Plan.OptionalChoice);

        h.Rings.InConfirmedRun = null;
        var dialog = new ChangePlanViewModel(plan.Plan, h.RingWriter().RunFor(plan)) { ExtraAcknowledged = true, OptionChosen = true };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        var sent = h.Rings.Applied.Single().Single();
        Assert.True(sent.CreateNowContentDistributing);
        Assert.All(sent.RingChanges, c => Assert.False(c.CreateNowContentDistributing));

        var noGroup = prepared with { RingChanges = [.. prepared.RingChanges.Select(c => c with { DpGroups = [] })] };
        Assert.Null(RingText.BuildCreateRunPlan(noGroup, h.Clock).Plan!.Plan.OptionalChoice);
    }

    // ---- P13: the ring writer over the real ring and deployment services -------------------------------

    private static readonly FixedClock LabClock = new(new DateTimeOffset(DeploymentHarness.UtcNow), DeploymentHarness.Local);

    private static RingWriter Writer(RingHarness h, RecordingSiteHistory? history = null) =>
        new(h.Service, history ?? new RecordingSiteHistory(), new RecordingMaintenanceLog(), () => "LAB", _ => Task.FromResult(true));

    private static async Task<ChangePlanViewModel> ConfirmAsync(RingWriter writer, RingRunPlan plan, bool acknowledge = true)
    {
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan)) { ExtraAcknowledged = acknowledge, DestructiveAcknowledged = true };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        return dialog;
    }

    private static async Task<RingChange> PreparedRunAsync(RingHarness h, RingPlan plan, DeployableObject obj) =>
        await h.PrepareAsync(RingHarness.CreateRun(plan, obj, await h.ExpandAsync(plan, obj)));

    [Fact]
    public async Task TheWriterCreatesARequiredRunOnlyWithTheSecondBox()
    {
        using var h = new RingHarness();
        var prepared = await PreparedRunAsync(h, RingHarness.ThreeRings(purpose: "Required"), DeploymentHarness.App);
        var (plan, problem) = RingText.BuildCreateRunPlan(prepared, LabClock);
        Assert.Null(problem);
        Assert.Equal("I understand that 3 Required rings enforce on 1 device in total at their deadlines.", plan!.Plan.ExtraConfirmation);
        Assert.Contains($"{DeploymentText.MeteredLabel}: no", plan.Plan.Items[0].Detail);

        var forged = new RingRunPlan(plan.Action, plan.Plan.Title, plan.Plan.ActionName, prepared, plan.Entries) { NeedsRequiredConfirmation = true };
        var refused = await ConfirmAsync(Writer(h), forged);
        Assert.All(refused.ResultRows, r => Assert.Equal(RingWriter.NotAcknowledgedText, r.Message));
        Assert.Empty(h.Site.List("AppAssignments"));

        var dialog = await ConfirmAsync(Writer(h), plan);
        Assert.All(dialog.ResultRows, r => Assert.True(r.Succeeded, r.Message));
        Assert.Equal(3, h.Site.List("AppAssignments").Count);
    }

    [Fact]
    public async Task TheWriterStopsAtAFailedSecondRingAndUndoRunRemovesTheFirstAndClosesTheRun()
    {
        using var h = new RingHarness();
        var prepared = await PreparedRunAsync(h, RingHarness.ThreeRings(), DeploymentHarness.App);
        var collections = h.Site.List("Collections");
        collections.Remove(collections.OfType<JsonObject>().Single(c => (string?)c["CollectionID"] == RingHarness.C2));
        var (plan, _) = RingText.BuildCreateRunPlan(prepared, LabClock);
        var history = new RecordingSiteHistory();
        var dialog = await ConfirmAsync(Writer(h, history), plan!);
        var rows = dialog.ResultRows;
        Assert.True(rows[0].Succeeded, rows[0].Message);
        Assert.False(rows[1].Succeeded);
        Assert.True(rows[2].Skipped);
        Assert.Single(h.Site.List("AppAssignments"));
        Assert.Equal(SiteOperationOutcome.PartlySucceeded, history.Entries.Single().Outcome);

        var run = (await h.Service.ListRunsAsync(false, CancellationToken.None)).Single();
        var undo = await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, run.RunId));
        var (undoPlan, problem) = RingText.BuildUndoRunPlan(undo, run.RunId, run);
        Assert.Null(problem);
        Assert.Equal(["Remove deployment", "Cancel held ring", "Cancel held ring", "Close run"], undoPlan!.Plan.Items.Select(i => i.Action));
        Assert.StartsWith("Ring QA: ", undoPlan.Plan.Items[0].Target);
        Assert.Equal(["Ring Pilot: DH-Test-2 (MCM0009A)", "Ring Prod: DH-Test-3 (MCM0009B)"], undoPlan.Plan.Items.Skip(1).Take(2).Select(i => i.Target));
        var undone = await ConfirmAsync(Writer(h), undoPlan);
        Assert.All(undone.ResultRows, r => Assert.True(r.Succeeded, r.Message));
        Assert.Empty(h.Site.List("AppAssignments"));
        Assert.True(h.Run(run.RunId).IsClosed);
    }

    [Fact]
    public async Task TheWriterPromotesTheNextHeldRingOfAHeldRun()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings(hold: true) with { Rings = [.. RingHarness.ThreeRings().Rings.Select(r => r with { SuccessThresholdPercent = null })] };
        var prepared = await PreparedRunAsync(h, plan, DeploymentHarness.Package);
        var (createPlan, _) = RingText.BuildCreateRunPlan(prepared, LabClock);
        Assert.Equal(["Create deployment", "Hold ring", "Hold ring"], createPlan!.Plan.Items.Select(i => i.Action));
        var created = await ConfirmAsync(Writer(h), createPlan);
        Assert.True(created.ResultRows[0].Succeeded, created.ResultRows[0].Message);
        Assert.Equal(RingWriter.HeldText, created.ResultRows[1].Message);

        var run = (await h.Service.ListRunsAsync(false, CancellationToken.None)).Single();
        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        var (promotePlan, problem) = RingText.BuildPromotePlan(promote, LabClock, run.TimeBasedOn);
        Assert.Null(problem);
        Assert.Contains("Fast network: ", promotePlan!.Plan.Items[0].Detail);
        var promoted = await ConfirmAsync(Writer(h), promotePlan);
        Assert.True(promoted.ResultRows.Single().Succeeded, promoted.ResultRows.Single().Message);
        Assert.Equal(2, h.Site.List("Adverts").Count);
        Assert.Equal([RingStatuses.Created, RingStatuses.Created, RingStatuses.Held], h.Run(run.RunId).Rings.Select(r => r.Status));
    }

    [Fact]
    public async Task TheWriterUndoesARunThatReconcileClosedAndTheRunStaysClosed()
    {
        using var h = new RingHarness();
        var prepared = await PreparedRunAsync(h, RingHarness.ThreeRings(), DeploymentHarness.App);
        var (plan, _) = RingText.BuildCreateRunPlan(prepared, LabClock);
        await ConfirmAsync(Writer(h), plan!);
        var run = (await h.Service.ListRunsAsync(false, CancellationToken.None)).Single();
        var closed = await h.Service.ReconcileAsync(run.RunId, CancellationToken.None);
        Assert.True(closed.IsClosed);

        var undo = await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, run.RunId));
        var (undoPlan, problem) = RingText.BuildUndoRunPlan(undo, run.RunId, closed);
        Assert.Null(problem);
        Assert.Equal(["Remove deployment", "Remove deployment", "Remove deployment"], undoPlan!.Plan.Items.Select(i => i.Action));
        Assert.Contains("The run is closed and stays closed.", undoPlan.Plan.Items[^1].Detail);
        var undone = await ConfirmAsync(Writer(h), undoPlan);
        Assert.All(undone.ResultRows, r => Assert.True(r.Succeeded, r.Message));
        Assert.Empty(h.Site.List("AppAssignments"));
        Assert.True(h.Run(run.RunId).IsClosed);
    }
}
