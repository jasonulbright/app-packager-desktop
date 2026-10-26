using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using CheckBox = System.Windows.Controls.CheckBox;
using Size = System.Windows.Size;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace ApplicationPackagerEngine.Tests;

public sealed class ChangePlanTests
{
    private static ChangePlan Plan(bool destructive = false, bool undo = false) => new(
        "Remove maintenance windows",
        "Remove 2 windows",
        [new ChangePlanItem("Remove", "Patch Tuesday", "Collection SMS00001"), new ChangePlanItem("Remove", "Weekend", null)],
        destructive,
        undo);

    private static ChangePlan FivePlan() => new(
        "Remove maintenance windows", "Remove 5 windows",
        Enumerable.Range(1, 5).Select(i => new ChangePlanItem("Remove", "Window " + i, null)));

    private static ChangePlanResult AllSucceeded(ConfirmedChangePlan confirmed) =>
        new(confirmed.Plan.Items.Select(i => new ChangePlanItemResult(i, true, "Removed.")).ToList());

    private static Task<ChangePlanResult> Succeed(ConfirmedChangePlan c, IProgress<ChangePlanItemResult> _, CancellationToken __) =>
        Task.FromResult(AllSucceeded(c));

    [Fact]
    public void AConfirmedPlanCannotBeMadeOutsideTheConfirmation()
    {
        var type = typeof(ConfirmedChangePlan);
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance), c => c.IsFamily || c.IsFamilyOrAssembly);
        Assert.True(type.IsSealed);
        Assert.DoesNotContain(type.Assembly.GetExportedTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)),
            m => m.ReturnType == type);
    }

    [Fact]
    public void TheIssueKeyHasOnlyAPrivateConstructorAndIssueRefusesAMissingKey()
    {
        var key = typeof(ChangePlanViewModel).GetNestedType("IssueKey", BindingFlags.NonPublic)!;
        Assert.NotNull(key);
        Assert.True(key.IsSealed);
        Assert.All(key.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), c => Assert.True(c.IsPrivate));
        Assert.DoesNotContain(key.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly),
            m => m.ReturnType == key);

        var issue = typeof(ConfirmedChangePlan).GetMethod("Issue", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failure = Assert.Throws<TargetInvocationException>(() => issue.Invoke(null, [null, Plan(), false]));
        Assert.IsType<ArgumentNullException>(failure.InnerException);
    }

    // Inside the UI assembly the compiler lets any file name the internal Issue method;
    // this scan is what keeps issuance in the dialog files.
    private static readonly string[] IssuanceAllowed =
    [
        Path.Combine("Services", "ChangePlan.cs"),
        Path.Combine("ViewModels", "Windows", "ChangePlanViewModel.cs")
    ];

    internal static bool ReferencesIssuance(string source) => IssuanceReference().IsMatch(source);

    private static Regex IssuanceReference() => new(
        @"ConfirmedChangePlan\s*\.\s*Issue\b|\bIssueKey\b|new\s+(?:[\w.]+\.)?ConfirmedChangePlan\s*\(|using\s+static\s+[\w.]*ConfirmedChangePlan\b|typeof\s*\(\s*(?:[\w.]+\.)?ConfirmedChangePlan\s*\)|nameof\s*\(\s*(?:[\w.]+\.)?ConfirmedChangePlan\s*\.\s*Issue",
        RegexOptions.CultureInvariant);

    private static string UiSourceRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "src", "ApplicationPackagerUI"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine(folder!.FullName, "src", "ApplicationPackagerUI");
    }

    [Fact]
    public void OnlyTheDialogFilesIssueOrConstructAConfirmedPlan()
    {
        var root = UiSourceRoot();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains(files, f => f.EndsWith(IssuanceAllowed[1], StringComparison.OrdinalIgnoreCase));

        var offenders = files
            .Where(f => !IssuanceAllowed.Any(a => f.EndsWith(Path.DirectorySeparatorChar + a, StringComparison.OrdinalIgnoreCase)))
            .Where(f => ReferencesIssuance(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Empty(offenders);
        Assert.True(ReferencesIssuance(File.ReadAllText(Path.Combine(root, IssuanceAllowed[1]))));
    }

    [Theory]
    [InlineData("var c = ConfirmedChangePlan.Issue(key, plan, true);")]
    [InlineData("var c = ApplicationPackagerUI.Services.ConfirmedChangePlan .Issue(k, p, false);")]
    [InlineData("ChangePlanViewModel.IssueKey k = null!;")]
    [InlineData("using static ApplicationPackagerUI.Services.ConfirmedChangePlan;")]
    [InlineData("Activator.CreateInstance(typeof(ConfirmedChangePlan), true);")]
    [InlineData("var c = new ConfirmedChangePlan(plan);")]
    public void TheIssuanceScanFlagsEachWayToForgeAConfirmation(string source)
    {
        Assert.True(ReferencesIssuance(source));
    }

    [Theory]
    [InlineData("public Task Run(ConfirmedChangePlan plan, CancellationToken ct) => _service.RemoveAsync(plan.Plan.Items, ct);")]
    [InlineData("ChangePlanOutcome outcome = _dialogs.Show(plan, RunAsync);")]
    public void TheIssuanceScanLetsAWritePageTakeAConfirmedPlan(string source)
    {
        Assert.False(ReferencesIssuance(source));
    }

    [Fact]
    public void TheRunDelegateTakesOnlyAConfirmedPlan()
    {
        var invoke = typeof(ChangePlanRun).GetMethod("Invoke")!;
        Assert.Equal(typeof(ConfirmedChangePlan), invoke.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void APlanNeedsAnActionNameAndAtLeastOneItem()
    {
        Assert.Throws<ArgumentException>(() => new ChangePlan("T", " ", [new ChangePlanItem("A", "B", null)]));
        Assert.Throws<ArgumentException>(() => new ChangePlan("T", "Go", []));
    }

    [Fact]
    public void APlanCopiesTheItemsItWasGiven()
    {
        var source = new List<ChangePlanItem> { new("Remove", "A", null) };
        var plan = new ChangePlan("T", "Go", source);
        source.Add(new ChangePlanItem("Remove", "B", null));
        Assert.Single(plan.Items);
    }

    [Fact]
    public async Task TheItemsOfAConfirmedPlanCannotBeChangedByACast()
    {
        ConfirmedChangePlan? confirmed = null;
        var plan = Plan();
        var vm = new ChangePlanViewModel(plan, (c, p, ct) => { confirmed = c; return Succeed(c, p, ct); });
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(confirmed);
        Assert.IsNotType<List<ChangePlanItem>>(confirmed!.Plan.Items);
        Assert.IsNotType<ChangePlanItem[]>(confirmed.Plan.Items);
        var list = Assert.IsAssignableFrom<IList<ChangePlanItem>>(confirmed.Plan.Items);
        Assert.Throws<NotSupportedException>(() => list.Add(new ChangePlanItem("Remove", "Injected", null)));
        Assert.Throws<NotSupportedException>(() => list[0] = new ChangePlanItem("Remove", "Injected", null));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.NotSame(plan.Items, confirmed.Plan.Items);
        Assert.Equal(plan.Items, confirmed.Plan.Items);
        Assert.Equal(2, confirmed.Plan.Items.Count);
    }

    [Fact]
    public async Task NothingRunsUntilConfirmAndConfirmRunsOnce()
    {
        var runs = 0;
        var vm = new ChangePlanViewModel(Plan(), (c, p, ct) => { runs++; return Succeed(c, p, ct); });

        Assert.Equal(0, runs);
        Assert.Equal(ChangePlanStage.Review, vm.Stage);
        Assert.Equal("Remove 2 windows", vm.ConfirmText);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("2 changes. Review the list, then confirm.", vm.SummaryText);
        Assert.True(vm.ConfirmCommand.CanExecute(null));

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, runs);
        Assert.Equal(ChangePlanStage.Done, vm.Stage);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.True(vm.Outcome.Confirmed);
        Assert.Equal("2 changed, 0 failed.", vm.ResultText);
        Assert.Equal(2, vm.ResultRows.Count);
        Assert.Equal(ChangePlanStop.Completed, vm.Outcome.Result!.Stop);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ADestructivePlanNeedsTheExtraCheckBeforeConfirmRuns()
    {
        var runs = 0;
        var vm = new ChangePlanViewModel(Plan(destructive: true), (c, p, ct) => { runs++; return Succeed(c, p, ct); });
        Assert.True(vm.IsDestructive);
        Assert.False(vm.ConfirmCommand.CanExecute(null));

        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(0, runs);
        Assert.Equal(ChangePlanStage.Review, vm.Stage);

        vm.DestructiveAcknowledged = true;
        Assert.True(vm.ConfirmCommand.CanExecute(null));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void CancelBeforeConfirmClosesWithoutARun()
    {
        var runs = 0;
        var vm = new ChangePlanViewModel(Plan(), (c, p, ct) => { runs++; return Succeed(c, p, ct); });
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.CancelCommand.Execute(null);
        Assert.True(closed);
        Assert.False(vm.Outcome.Confirmed);
        Assert.Null(vm.Outcome.Result);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task CancelDuringTheRunCancelsItsTokenAndReportsIt()
    {
        var started = new TaskCompletionSource();
        var vm = new ChangePlanViewModel(Plan(), async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var run = vm.ConfirmCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanClose);

        vm.CancelCommand.Execute(null);
        await run;

        Assert.Equal(ChangePlanStage.Done, vm.Stage);
        Assert.Equal("The run was cancelled. Changes made before the cancel stay in place.", vm.ResultText);
        Assert.True(vm.CanClose);
    }

    private sealed class RecordingLog : IAppLog
    {
        public List<AppLogEntry> Entries { get; } = [];
        public string? SessionFile => null;
        public AppLogLevel MinimumLevel { get; set; } = AppLogLevel.Debug;
        public event EventHandler<AppLogEntry>? Written;
        public void RegisterSecret(string value) { }
        public void Write(AppLogLevel level, string area, string message)
        {
            var entry = new AppLogEntry(DateTimeOffset.UtcNow, level, area, message);
            lock (Entries) Entries.Add(entry);
            Written?.Invoke(this, entry);
        }
        public IReadOnlyList<AppLogEntry> Tail() { lock (Entries) return Entries.ToList(); }
    }

    [Fact]
    public async Task AFailedRunShowsAFixedLineAndLogsTheMaskedDetail()
    {
        var log = new RecordingLog();
        var vm = new ChangePlanViewModel(Plan(), (_, _, _) => throw new InvalidOperationException("The SMS Provider denied access. password=Zq9secret"), log: log);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(ChangePlanViewModel.FailedText, vm.ResultText);
        Assert.Equal("The run failed. The log has the details.", ChangePlanViewModel.FailedText);
        Assert.True(vm.Outcome.Confirmed);
        Assert.False(vm.UndoCommand.CanExecute(null));
        var entry = Assert.Single(log.Entries, e => e.Level == AppLogLevel.Error);
        Assert.Contains("The SMS Provider denied access.", entry.Message);
        Assert.DoesNotContain("Zq9secret", entry.Message);
    }

    [Fact]
    public async Task AFailedRunWithoutALogStillShowsTheFixedLine()
    {
        var vm = new ChangePlanViewModel(Plan(), (_, _, _) => throw new InvalidOperationException("raw detail"));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(ChangePlanViewModel.FailedText, vm.ResultText);
    }

    [Fact]
    public async Task ItemMessagesStayVisibleButPassTheSecretMask()
    {
        var vm = new ChangePlanViewModel(Plan(), (c, _, _) => Task.FromResult(new ChangePlanResult(
            [new ChangePlanItemResult(c.Plan.Items[0], true, "Removed."), new ChangePlanItemResult(c.Plan.Items[1], false, "Denied for token=Zq9secret on CM01.")])));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("Removed.", vm.ResultRows[0].Message);
        Assert.Equal("Denied for token=*** on CM01.", vm.ResultRows[1].Message);
    }

    [Fact]
    public async Task ARunThatChangesThreeOfFiveAndThenThrowsKeepsThePartialResult()
    {
        var plan = new ChangePlan("Remove maintenance windows", "Remove 5 windows",
            Enumerable.Range(1, 5).Select(i => new ChangePlanItem("Remove", "Window " + i, null)), hasUndo: true);
        ConfirmedChangePlan? undone = null;
        var vm = new ChangePlanViewModel(plan,
            (c, progress, _) =>
            {
                for (var i = 0; i < 3; i++) progress.Report(new ChangePlanItemResult(c.Plan.Items[i], true, "Removed."));
                throw new InvalidOperationException("The SMS Provider stopped answering.");
            },
            (c, _, _) => { undone = c; return Task.FromResult(new ChangePlanResult([])); });

        await vm.ConfirmCommand.ExecuteAsync(null);

        var result = vm.Outcome.Result;
        Assert.NotNull(result);
        Assert.Equal(ChangePlanStop.Failed, result!.Stop);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal(3, result.SucceededCount);
        Assert.Equal(["Window 1", "Window 2", "Window 3"], result.Items.Where(i => i.Succeeded).Select(i => i.Item.Target));
        Assert.All(result.Items.Where(i => !i.Succeeded), i => Assert.Equal(ChangePlanViewModel.FailedReason, i.Message));
        Assert.Equal(["Window 4", "Window 5"], result.Items.Where(i => !i.Succeeded).Select(i => i.Item.Target));
        Assert.Equal(5, vm.ResultRows.Count);
        Assert.Equal(ChangePlanViewModel.FailedText + " Before the failure: 3 changed, 0 failed.", vm.ResultText);

        Assert.True(vm.ShowUndo);
        Assert.True(vm.UndoCommand.CanExecute(null));
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.NotNull(undone);
    }

    [Fact]
    public async Task ACancelledRunKeepsTheOutcomesReportedBeforeTheCancel()
    {
        var reported = new TaskCompletionSource();
        var vm = new ChangePlanViewModel(FivePlan(), async (c, progress, ct) =>
        {
            progress.Report(new ChangePlanItemResult(c.Plan.Items[0], true, "Removed."));
            progress.Report(new ChangePlanItemResult(c.Plan.Items[1], false, "Not found."));
            reported.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return AllSucceeded(c);
        });
        var run = vm.ConfirmCommand.ExecuteAsync(null);
        await reported.Task;
        vm.CancelCommand.Execute(null);
        await run;

        var result = vm.Outcome.Result!;
        Assert.Equal(ChangePlanStop.Cancelled, result.Stop);
        Assert.Equal(["Changed", "Failed", "Failed", "Failed", "Failed"], result.Items.Select(i => i.OutcomeText));
        Assert.Equal("Not found.", result.Items[1].Message);
        Assert.All(result.Items.Skip(2), i => Assert.Equal(ChangePlanViewModel.CancelledReason, i.Message));
        Assert.Equal(ChangePlanViewModel.CancelledText + " Before the cancel: 1 changed, 1 failed.", vm.ResultText);
        Assert.Equal(5, vm.ResultRows.Count);
    }

    [Fact]
    public async Task ARunThatIgnoresItsCancelOffersAForceCloseThatReturnsThePartialOutcome()
    {
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var vm = new ChangePlanViewModel(FivePlan(), async (c, progress, _) =>
        {
            progress.Report(new ChangePlanItemResult(c.Plan.Items[0], true, "Removed."));
            reported.SetResult();
            await release.Task;
            return AllSucceeded(c);
        }, forceCloseDelay: TimeSpan.FromMilliseconds(150));
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;
        var run = vm.ConfirmCommand.ExecuteAsync(null);
        await reported.Task;

        Assert.False(vm.ShowForceClose);
        Assert.False(vm.ForceCloseCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await Task.Delay(30);
        Assert.False(vm.ShowForceClose);
        Assert.False(vm.CanClose);

        await WaitUntil(() => vm.ShowForceClose);
        Assert.True(vm.IsBusy);
        Assert.True(vm.ForceCloseCommand.CanExecute(null));

        vm.ForceCloseCommand.Execute(null);

        Assert.Equal(1, closed);
        Assert.True(vm.IsDetached);
        Assert.True(vm.CanClose);
        var result = vm.Outcome.Result!;
        Assert.True(vm.Outcome.Confirmed);
        Assert.Equal(ChangePlanStop.Detached, result.Stop);
        Assert.Equal(1, result.SucceededCount);
        Assert.All(result.Items.Skip(1), i => Assert.Equal(ChangePlanViewModel.DetachedReason, i.Message));

        // The run's late result never replaces what the caller already received.
        release.SetResult();
        await run;
        Assert.Same(result, vm.Outcome.Result);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task ARunThatStopsAfterTheCancelNeverOffersAForceClose()
    {
        var started = new TaskCompletionSource();
        var vm = new ChangePlanViewModel(Plan(), async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }, forceCloseDelay: TimeSpan.FromMilliseconds(50));
        var run = vm.ConfirmCommand.ExecuteAsync(null);
        await started.Task;
        vm.CancelCommand.Execute(null);
        await run;
        await Task.Delay(150);
        Assert.False(vm.ShowForceClose);
        Assert.False(vm.IsDetached);
    }

    [Fact]
    public async Task APartlyFailedRunCountsBoth()
    {
        var vm = new ChangePlanViewModel(Plan(), (c, _, _) => Task.FromResult(new ChangePlanResult(
            [new ChangePlanItemResult(c.Plan.Items[0], true, "Removed."), new ChangePlanItemResult(c.Plan.Items[1], false, "Not found.")])));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("1 changed, 1 failed.", vm.ResultText);
        Assert.Equal(["Changed", "Failed"], vm.ResultRows.Select(r => r.OutcomeText));
    }

    [Fact]
    public async Task UndoIsOfferedOnlyWhenThePlanDeclaresItAndRunsWithTheSameConfirmedPlan()
    {
        ConfirmedChangePlan? ran = null, undone = null;
        var vm = new ChangePlanViewModel(Plan(undo: true),
            (c, _, _) => { ran = c; return Task.FromResult(AllSucceeded(c)); },
            (c, _, _) => { undone = c; return Task.FromResult(new ChangePlanResult(c.Plan.Items.Select(i => new ChangePlanItemResult(i, true, "Restored.")).ToList())); });
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.ShowUndo);

        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.True(vm.ShowUndo);
        Assert.True(vm.UndoCommand.CanExecute(null));

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Same(ran, undone);
        Assert.Equal(ChangePlanStage.Undone, vm.Stage);
        Assert.Equal("Undo: 2 changed, 0 failed.", vm.ResultText);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.NotNull(vm.Outcome.UndoResult);
    }

    [Fact]
    public async Task CancelRemainsVisibleAndCancelsAnUndoRun()
    {
        var undoStarted = new TaskCompletionSource();
        var vm = new ChangePlanViewModel(Plan(undo: true), Succeed, async (_, _, ct) =>
        {
            undoStarted.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.False(vm.ShowCancel);
        var undo = vm.UndoCommand.ExecuteAsync(null);
        await undoStarted.Task;
        Assert.Equal(ChangePlanStage.Undoing, vm.Stage);
        Assert.True(vm.ShowCancel);

        vm.CancelCommand.Execute(null);
        await undo;

        Assert.Equal(ChangePlanStop.Cancelled, vm.Outcome.UndoResult!.Stop);
        Assert.False(vm.ShowCancel);
    }

    [Fact]
    public void UndoMustMatchThePlan()
    {
        Assert.Throws<ArgumentException>(() => new ChangePlanViewModel(Plan(undo: true), Succeed));
        Assert.Throws<ArgumentException>(() => new ChangePlanViewModel(Plan(undo: false), Succeed, Succeed));
    }

    [Fact]
    public async Task APlanWithoutUndoNeverShowsUndo()
    {
        var vm = new ChangePlanViewModel(Plan(), Succeed);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.False(vm.ShowUndo);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task CloseAfterTheRunClosesTheWindow()
    {
        var vm = new ChangePlanViewModel(Plan(), Succeed);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        Assert.False(vm.CloseCommand.CanExecute(null));
        await vm.ConfirmCommand.ExecuteAsync(null);
        vm.CloseCommand.Execute(null);
        Assert.True(closed);
    }

    [Fact]
    public void APageReachesTheDialogThroughTheInterface()
    {
        var fake = new FakeDialogs();
        IChangePlanDialogs dialogs = fake;
        var outcome = dialogs.Show(Plan(), Succeed);
        Assert.False(outcome.Confirmed);
        Assert.Equal("Remove 2 windows", fake.Shown!.ActionName);
    }

    private sealed class FakeDialogs : IChangePlanDialogs
    {
        public ChangePlan? Shown { get; private set; }
        public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
        {
            Shown = plan;
            return ChangePlanOutcome.NotConfirmed;
        }
    }

    private static ChangePlanWindow Rendered(ChangePlanViewModel vm)
    {
        var window = new ChangePlanWindow(vm);
        window.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        window.Resources.MergedDictionaries.Add(new ControlsDictionary());
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(820, 520));
        content.Arrange(new Rect(0, 0, 820, 520));
        content.UpdateLayout();
        return window;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static string InlineText(IEnumerable<System.Windows.Documents.Inline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        System.Windows.Documents.Run run => run.Text,
        System.Windows.Documents.Span span => InlineText(span.Inlines),
        _ => ""
    }));

    private static void Click(System.Windows.Controls.Button button)
    {
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
        invoke.Invoke();
        StaTestHelper.Flush();
    }

    [Fact]
    public void TheRenderedConfirmButtonShowsTheActionNameLiterallyWithoutAnAccessKey()
    {
        var vm = new ChangePlanViewModel(new ChangePlan("T", "Delete CM_ABC", [new ChangePlanItem("Delete", "x", null)]), Succeed);
        StaTestHelper.Run(() =>
        {
            var window = Rendered(vm);
            var confirm = (System.Windows.Controls.Button)window.FindName("btnConfirm");
            var access = Descendants(confirm).OfType<AccessText>().Single();
            Assert.Equal('\0', access.AccessKey);
            var shown = Descendants(access).OfType<TextBlock>().Single();
            Assert.Equal("Delete CM_ABC", InlineText(shown.Inlines));
        });
    }

    [Fact]
    public void ClickingTheRenderedConfirmButtonRunsThePlanAndItIsNeverTheDefaultButton()
    {
        var runs = 0;
        var vm = new ChangePlanViewModel(Plan(), (c, p, ct) => { runs++; return Succeed(c, p, ct); });
        StaTestHelper.Run(() =>
        {
            var window = Rendered(vm);
            var confirm = (System.Windows.Controls.Button)window.FindName("btnConfirm");
            Assert.False(confirm.IsDefault);
            Assert.True(((System.Windows.Controls.Button)window.FindName("btnCancel")).IsCancel);
            Assert.Equal(0, runs);

            Click(confirm);

            Assert.Equal(1, runs);
            Assert.Equal(ChangePlanStage.Done, vm.Stage);
        });
    }

    [Fact]
    public void TheRenderedDestructiveCheckGatesConfirmAndUndoRunsTheUndo()
    {
        var undone = 0;
        var vm = new ChangePlanViewModel(Plan(destructive: true, undo: true), Succeed,
            (c, p, ct) => { undone++; return Succeed(c, p, ct); });
        StaTestHelper.Run(() =>
        {
            var window = Rendered(vm);
            var confirm = (System.Windows.Controls.Button)window.FindName("btnConfirm");
            var check = (CheckBox)window.FindName("chkDestructive");
            Assert.False(confirm.IsEnabled);

            check.IsChecked = true;
            Assert.True(vm.DestructiveAcknowledged);
            Assert.True(confirm.IsEnabled);
            Click(confirm);

            var undo = (System.Windows.Controls.Button)window.FindName("btnUndo");
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, undo.Visibility);
            Click(undo);
            Assert.Equal(1, undone);
            Assert.Equal(ChangePlanStage.Undone, vm.Stage);
        });
    }

    [Fact]
    public async Task TheWindowShowsTheCheckOnlyForADestructivePlanAndUndoOnlyAfterTheRun()
    {
        var vm = new ChangePlanViewModel(Plan(destructive: true, undo: true), Succeed, Succeed);
        var safe = new ChangePlanViewModel(Plan(), Succeed);

        StaTestHelper.Run(() =>
        {
            var window = new ChangePlanWindow(vm);
            var check = (CheckBox)window.FindName("chkDestructive");
            var undo = (FrameworkElement)window.FindName("btnUndo");
            Assert.Equal(Visibility.Visible, check.Visibility);
            Assert.Equal(Visibility.Collapsed, undo.Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("btnForceClose")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((CheckBox)new ChangePlanWindow(safe).FindName("chkDestructive")).Visibility);
        });

        var done = new ChangePlanViewModel(Plan(destructive: true, undo: true), Succeed, Succeed) { DestructiveAcknowledged = true };
        await done.ConfirmCommand.ExecuteAsync(null);
        StaTestHelper.Run(() =>
        {
            var window = new ChangePlanWindow(done);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("btnUndo")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("btnConfirm")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("gridResult")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("gridPlan")).Visibility);
        });
    }

    [Fact]
    public async Task TheWindowShowsTheForceCloseChoiceWhenTheViewModelOffersIt()
    {
        // The run never completes: its completion would update collections bound on the ended STA thread.
        var started = new TaskCompletionSource();
        var vm = new ChangePlanViewModel(Plan(), async (c, _, _) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
            return AllSucceeded(c);
        }, forceCloseDelay: TimeSpan.FromMilliseconds(20));
        _ = vm.ConfirmCommand.ExecuteAsync(null);
        await started.Task;
        vm.RequestCancel();
        await WaitUntil(() => vm.ShowForceClose);

        StaTestHelper.Run(() =>
        {
            var window = new ChangePlanWindow(vm);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("btnForceClose")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("txtForceClose")).Visibility);
        });
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met.");
            await Task.Delay(10);
        }
    }
}
