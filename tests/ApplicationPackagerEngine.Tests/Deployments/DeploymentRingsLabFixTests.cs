using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ApplicationPackagerEngine.Services;
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
