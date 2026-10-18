using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Xml.Linq;
using CheckBox = System.Windows.Controls.CheckBox;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerEngine.Tests;

public sealed class ChangePlanTests
{
    private static ChangePlan Plan(bool destructive = false, bool undo = false) => new(
        "Remove maintenance windows",
        "Remove 2 windows",
        [new ChangePlanItem("Remove", "Patch Tuesday", "Collection SMS00001"), new ChangePlanItem("Remove", "Weekend", null)],
        destructive,
        undo);

    private static ChangePlanResult AllSucceeded(ConfirmedChangePlan confirmed) =>
        new(confirmed.Plan.Items.Select(i => new ChangePlanItemResult(i, true, "Removed.")).ToList());

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
    public async Task NothingRunsUntilConfirmAndConfirmRunsOnce()
    {
        var runs = 0;
        var vm = new ChangePlanViewModel(Plan(), (c, _) => { runs++; return Task.FromResult(AllSucceeded(c)); });

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
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ADestructivePlanNeedsTheExtraCheckBeforeConfirmRuns()
    {
        var runs = 0;
        var vm = new ChangePlanViewModel(Plan(destructive: true), (c, _) => { runs++; return Task.FromResult(AllSucceeded(c)); });
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
        var vm = new ChangePlanViewModel(Plan(), (c, _) => { runs++; return Task.FromResult(AllSucceeded(c)); });
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
        var vm = new ChangePlanViewModel(Plan(), async (_, ct) =>
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

    [Fact]
    public async Task AFailedRunReportsItsMessage()
    {
        var vm = new ChangePlanViewModel(Plan(), (_, _) => throw new InvalidOperationException("The SMS Provider denied access."));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("The run failed: The SMS Provider denied access.", vm.ResultText);
        Assert.True(vm.Outcome.Confirmed);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task APartlyFailedRunCountsBoth()
    {
        var vm = new ChangePlanViewModel(Plan(), (c, _) => Task.FromResult(new ChangePlanResult(
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
            (c, _) => { ran = c; return Task.FromResult(AllSucceeded(c)); },
            (c, _) => { undone = c; return Task.FromResult(new ChangePlanResult(c.Plan.Items.Select(i => new ChangePlanItemResult(i, true, "Restored.")).ToList())); });
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
    public void UndoMustMatchThePlan()
    {
        Assert.Throws<ArgumentException>(() => new ChangePlanViewModel(Plan(undo: true), (c, _) => Task.FromResult(AllSucceeded(c))));
        Assert.Throws<ArgumentException>(() => new ChangePlanViewModel(Plan(undo: false), (c, _) => Task.FromResult(AllSucceeded(c)), (c, _) => Task.FromResult(AllSucceeded(c))));
    }

    [Fact]
    public async Task APlanWithoutUndoNeverShowsUndo()
    {
        var vm = new ChangePlanViewModel(Plan(), (c, _) => Task.FromResult(AllSucceeded(c)));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.False(vm.ShowUndo);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task CloseAfterTheRunClosesTheWindow()
    {
        var vm = new ChangePlanViewModel(Plan(), (c, _) => Task.FromResult(AllSucceeded(c)));
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        Assert.False(vm.CloseCommand.CanExecute(null));
        await vm.ConfirmCommand.ExecuteAsync(null);
        vm.CloseCommand.Execute(null);
        Assert.True(closed);
    }

    [Fact]
    public void AnUnderscoreInTheActionNameIsNotAnAccessKey()
    {
        var vm = new ChangePlanViewModel(new ChangePlan("T", "Delete CM_ABC", [new ChangePlanItem("Delete", "x", null)]), (c, _) => Task.FromResult(AllSucceeded(c)));
        Assert.Equal("Delete CM__ABC", vm.ConfirmContent);
    }

    [Fact]
    public void APageReachesTheDialogThroughTheInterface()
    {
        var fake = new FakeDialogs();
        IChangePlanDialogs dialogs = fake;
        var outcome = dialogs.Show(Plan(), (c, _) => Task.FromResult(AllSucceeded(c)));
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

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Markup()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        static string Relative(DirectoryInfo f) => Path.Combine(f.FullName, "src", "ApplicationPackagerUI", "Views", "Windows", "ChangePlanWindow.xaml");
        while (folder is not null && !File.Exists(Relative(folder))) folder = folder.Parent;
        Assert.NotNull(folder);
        return XDocument.Load(Relative(folder));
    }

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void TheWindowBindsTheConfirmButtonToTheActionAndNeverMakesItTheDefault()
    {
        var doc = Markup();
        var confirm = Named(doc, "btnConfirm");
        Assert.Equal("{Binding ConfirmContent}", (string?)confirm.Attribute("Content"));
        Assert.Equal("{Binding ConfirmCommand}", (string?)confirm.Attribute("Command"));
        Assert.Null(confirm.Attribute("IsDefault"));
        Assert.Equal("True", (string?)Named(doc, "btnCancel").Attribute("IsCancel"));
        Assert.Equal("{Binding DestructiveAcknowledged}", (string?)Named(doc, "chkDestructive").Attribute("IsChecked"));
        Assert.Equal("{Binding UndoCommand}", (string?)Named(doc, "btnUndo").Attribute("Command"));
    }

    [Fact]
    public async Task TheWindowShowsTheCheckOnlyForADestructivePlanAndUndoOnlyAfterTheRun()
    {
        var vm = new ChangePlanViewModel(Plan(destructive: true, undo: true),
            (c, _) => Task.FromResult(AllSucceeded(c)), (c, _) => Task.FromResult(AllSucceeded(c)));
        var safe = new ChangePlanViewModel(Plan(), (c, _) => Task.FromResult(AllSucceeded(c)));

        RunSta(() =>
        {
            var window = new ChangePlanWindow(vm);
            var check = (CheckBox)window.FindName("chkDestructive");
            var undo = (FrameworkElement)window.FindName("btnUndo");
            var confirm = (System.Windows.Controls.Button)window.FindName("btnConfirm");
            Assert.Equal(Visibility.Visible, check.Visibility);
            Assert.Equal(Visibility.Collapsed, undo.Visibility);
            Assert.Equal("Remove 2 windows", ((string)confirm.Content).Replace("__", "_"));
            Assert.Equal(Visibility.Collapsed, ((CheckBox)new ChangePlanWindow(safe).FindName("chkDestructive")).Visibility);
        });

        var done = new ChangePlanViewModel(Plan(destructive: true, undo: true),
            (c, _) => Task.FromResult(AllSucceeded(c)), (c, _) => Task.FromResult(AllSucceeded(c)))
        { DestructiveAcknowledged = true };
        await done.ConfirmCommand.ExecuteAsync(null);
        RunSta(() =>
        {
            var window = new ChangePlanWindow(done);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("btnUndo")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("btnConfirm")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("gridResult")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("gridPlan")).Visibility);
        });
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
