using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataGrid = System.Windows.Controls.DataGrid;
using RadioButton = System.Windows.Controls.RadioButton;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The plan editor purpose choice, the removal question size and the ring texts in real windows and over the page.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DeploymentRingsLabFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-ringlab-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static CollectionRow Row(string id, string name) => new(CollectionId: id, Name: name, FolderPath: "Rings", LimitingCollectionId: "SMS00001",
        LimitingCollectionName: "All Systems", MemberCount: 10, IsBuiltIn: false, RefreshType: "Periodic", Comment: "", DirectRuleCount: 0, QueryRuleCount: 0,
        IncludeRuleCount: 0, ExcludeRuleCount: 0);

    private static readonly CollectionRow[] Collections = [Row("LAB00010", "Pilot"), Row("LAB00011", "Wave 1"), Row("LAB00012", "Wave 2")];

    private static RingPlan Plan(string name, bool required) => new(Name: name, Description: "Three rings.", HoldLaterRings: false, TimeBasedOn: "LocalTime",
        Rings: Collections.Select((c, i) => FakeRingService.Ring(c.Name, c.CollectionId, c.Name, required ? "Required" : "Available", i * 7,
            required ? i * 7 + 2 : null)).ToList(), IsSeed: false);

    private static void Click(ButtonBase button) =>
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, null);

    private static void WithEditor(ApplicationTheme theme, RingPlanEditorViewModel editor, Action<RingPlanEditorWindow> test) =>
        WithApplication(theme, () =>
        {
            var window = new RingPlanEditorWindow(editor);
            Show(window);
            try
            {
                Settle(window);
                test(window);
            }
            finally
            {
                editor.GetType().GetProperty(nameof(RingPlanEditorViewModel.Saved))!.SetValue(editor, true);
                window.Close();
            }
        });

    private static RingPlanEditorViewModel Editor(RingPlan plan, bool copy, Func<RingPlanEditorViewModel, RingPlan, Task<bool>>? save = null, Func<bool>? discard = null) =>
        new(plan, copy, [plan], Collections, save ?? ((_, _) => Task.FromResult(true)), _ => null, confirmDiscard: discard);

    private static void Select(Window window, RingPlanEditorViewModel editor, int index)
    {
        ById<DataGrid>(window, "dgEditorRings").SelectedItem = editor.Rings[index];
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ARingSetRequiredStaysRequiredWhenAnotherRingIsSelected(ApplicationTheme theme)
    {
        var editor = Editor(Plan("Three Rings", required: false), copy: false);
        WithEditor(theme, editor, window =>
        {
            Select(window, editor, 0);
            Click(ById<RadioButton>(window, "rbRingRequired"));
            Settle(window);
            editor.Rings[0].DeadlineOffsetText = "2";
            Assert.Equal("Required", editor.Rings[0].Purpose);

            Select(window, editor, 1);
            Assert.Equal("Required", editor.Rings[0].Purpose);
            Assert.Equal("Available", editor.Rings[1].Purpose);
            Assert.True(ById<RadioButton>(window, "rbRingAvailable").IsChecked);

            Select(window, editor, 0);
            Assert.Equal("Required", editor.Rings[0].Purpose);
            Assert.True(ById<RadioButton>(window, "rbRingRequired").IsChecked);
            Assert.False(ById<RadioButton>(window, "rbRingAvailable").IsChecked);

            Select(window, editor, 2);
            Click(ById<RadioButton>(window, "rbRingAvailable"));
            Select(window, editor, 0);
            Assert.Equal(["Required", "Available", "Available"], editor.Rings.Select(r => r.Purpose));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, false, false)]
    [InlineData(ApplicationTheme.Dark, false, true)]
    [InlineData(ApplicationTheme.Light, true, false)]
    [InlineData(ApplicationTheme.Dark, true, true)]
    public void EditAndCopyShowThePurposeOfTheFileAndAskNothingWithoutAChange(ApplicationTheme theme, bool required, bool copy)
    {
        var plan = Plan("Three Rings", required);
        var asked = 0;
        var editor = Editor(plan, copy, discard: () => { asked++; return false; });
        WithEditor(theme, editor, window =>
        {
            for (var i = 0; i < 3; i++)
            {
                Select(window, editor, i);
                Assert.Equal(plan.Rings[i].Purpose, editor.Rings[i].Purpose);
                Assert.Equal(required, ById<RadioButton>(window, "rbRingRequired").IsChecked);
                Assert.Equal(!required, ById<RadioButton>(window, "rbRingAvailable").IsChecked);
            }
            Select(window, editor, 0);
            Assert.Equal(plan.Rings.Select(r => r.Purpose), editor.Rings.Select(r => r.Purpose));
            Assert.False(editor.HasChanges);
            Assert.True(editor.ConfirmClose());
            Assert.Equal(0, asked);
        });
    }

    [Fact]
    public void ANewPlanStartsWithoutRingsTakes100CollectionsAndRefusesAnEmptySave()
    {
        var chosen = Enumerable.Range(1, 100).Select(i => Row($"LABH{i:0000}", $"Wave {i}")).ToList();
        var editor = new RingPlanEditorViewModel(null, false, [], Collections, (_, _) => Task.FromResult(true), _ => null, () => chosen);
        Assert.Empty(editor.Rings);
        Assert.Null(editor.SelectedRing);
        Assert.False(editor.HasChanges);
        editor.Name = "Empty";
        Assert.Null(editor.Build(out var problem));
        Assert.Equal(RingPlanEditorViewModel.NoRings, problem);

        editor.DaysBetweenText = "1";
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(100, editor.Rings.Count);
        Assert.Equal("Wave 1", editor.Rings[0].Name);
        Assert.NotNull(editor.Build(out problem));
        Assert.Null(problem);
    }

    private static DeploymentChange Removal(int i) => new(DeploymentChangeKind.RemoveDeployment, null, null, null, [], false, false, false,
        new DeploymentRecord(DeploymentId: $"{{DEP-{i:000}}}", Type: DeploymentObjectType.Package, ObjectId: "LAB000A0", ObjectName: "Contoso Fonts 1.0",
            CollectionId: $"LABC{i:0000}", CollectionName: $"RG-Test-C{i:000}", Purpose: "Available", AvailableTime: new DateTime(2026, 10, 19, 8, 0, 0), Deadline: null,
            Created: new DateTime(2026, 10, 19, 8, 0, 0), CreatedBy: @"LAB\admin"), [], [], null);

    [Fact]
    public void TheRemovalQuestionOfManyDeploymentsStatesTheCountAndListsOneDeploymentPerLine()
    {
        var text = DeploymentWriter.RemovalQuestion(Enumerable.Range(1, 100).Select(Removal).ToList());
        var lines = text.Split(Environment.NewLine);
        Assert.Equal("Remove these 100 deployments? Software that clients already installed stays installed.", lines[0]);
        Assert.Equal(101, lines.Length);
        Assert.Equal("Contoso Fonts 1.0 from RG-Test-C100 (LABC0100)", lines[^1]);
        Assert.Equal("Remove the deployment Contoso Fonts 1.0 from RG-Test-C001 (LABC0001)? Software that clients already installed stays installed.",
            DeploymentWriter.RemovalQuestion([Removal(1)]));
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1080 - 48)]
    [InlineData(ApplicationTheme.Dark, 1080 - 48)]
    [InlineData(ApplicationTheme.Light, 768 - 40)]
    [InlineData(ApplicationTheme.Dark, 768 - 40)]
    public void TheRemovalQuestionOf100DeploymentsScrollsAndKeepsItsButtonsInsideTheWorkArea(ApplicationTheme theme, double workArea)
    {
        WithApplication(theme, () =>
        {
            var window = new RemovalConfirmWindow(DeploymentWriter.RemovalQuestion(Enumerable.Range(1, 100).Select(Removal).ToList()));
            window.FitWorkArea(workArea);
            Show(window);
            try
            {
                Settle(window);
                Assert.True(window.ActualHeight <= workArea + 0.5, $"The question is {window.ActualHeight:0} px high in a {workArea:0} px work area.");
                foreach (var id in new[] { "btnRemovalConfirm", "btnRemovalCancel" })
                    AssertInside(window, ById<FrameworkElement>(window, id), window.ActualHeight, id);
                var scroller = ById<ScrollViewer>(window, "scrollRemovalQuestion");
                Assert.True(scroller.ScrollableHeight > 0, "The list of 100 deployments does not scroll.");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheChangePlanOf100RingsKeepsItsCheckBoxesAndButtonsVisible(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var rows = Enumerable.Range(1, 100).Select(i => new RingExpansionRow(Index: i, Name: $"Wave {i} of the finance department rollout", CollectionId: $"LABC{i:0000}",
                CollectionName: $"Finance {i}", MemberCount: 10, Purpose: "Required", AvailableTime: new DateTime(2026, 10, 1), Deadline: new DateTime(2026, 10, 3),
                DpGroup: "", SuccessThresholdPercent: null, Checks: [])).ToList();
            var extra = RingText.SecondConfirmation(100, 1000, null, rows, "local time");
            Assert.DoesNotContain("Wave 50", extra);
            var items = Enumerable.Range(1, 100).Select(i => new ChangePlanItem("Remove deployment", $"Ring Wave {i}: Contoso Fonts 1.0 from Finance {i} (LABC{i:0000})",
                string.Join(Environment.NewLine, Enumerable.Repeat("A line of detail that the plan shows for this ring.", 6)))).ToList();
            var plan = new ChangePlanWindow(new ChangePlanViewModel(new ChangePlan("Undo ring run", "Remove 100 deployments", items, isDestructive: true) { ExtraConfirmation = extra },
                (_, _, _) => Task.FromResult(new ChangePlanResult([]))));
            Show(plan);
            try
            {
                plan.Height = 768 - 40;
                Settle(plan);
                foreach (var id in new[] { "chkChangePlanDestructive", "chkChangePlanExtra", "btnChangePlanConfirm", "btnChangePlanCancel" })
                    AssertInside(plan, ById<FrameworkElement>(plan, id), plan.ActualHeight, id);
            }
            finally { plan.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACopySavedUnchangedEqualsTheOriginalFileExceptTheName(bool required)
    {
        var store = new RingPlanStore(() => _root);
        store.List();
        var original = store.Save(Plan("Lab Plan", required), replace: false);
        var listed = store.List().Single(p => p.Name == "Lab Plan");
        RingPlan? saved = null;
        var editor = Editor(listed, copy: true, save: (_, p) => { saved = store.Save(p, replace: false); return Task.FromResult(true); });
        WithEditor(ApplicationTheme.Light, editor, window =>
        {
            Select(window, editor, 1);
            Select(window, editor, 0);
            Assert.False(editor.HasChanges);
            Run(editor.SaveCommand.ExecuteAsync(null));
            Assert.Equal("", editor.ValidationMessage);
        });
        Assert.NotNull(saved);
        var first = File.ReadAllText(Path.Combine(_root, original.FileName!));
        var second = File.ReadAllText(Path.Combine(_root, saved!.FileName!));
        Assert.Equal(first.Replace("\"Lab Plan\"", "\"Lab Plan copy\"", StringComparison.Ordinal), second);
    }
}
