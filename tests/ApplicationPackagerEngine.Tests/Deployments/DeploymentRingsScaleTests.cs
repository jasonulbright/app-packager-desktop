using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>Plans and runs of any ring count up to the cap, the plan editor helpers and the writer-only rule of the ring service.</summary>
public sealed class DeploymentRingsScaleTests
{
    private readonly DeploymentPageHarness _h = new();

    private static string Id(int i) => "LABR" + i.ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>A plan of <paramref name="count"/> rings on their own collections; ring 1 is Available, the others Required with growing deadlines.</summary>
    private RingPlan PlanOf(int count, string name = "Wide-Rings")
    {
        var rings = Enumerable.Range(1, count).Select(i => FakeRingService.Ring($"Wave {i}", Id(i), $"Wave {i} devices",
            i == 1 ? "Available" : "Required", i, i == 1 ? null : i + 2)).ToList();
        foreach (var i in Enumerable.Range(1, count)) _h.Rings.MemberCounts[Id(i)] = 10;
        var plan = new RingPlan(Name: name, Description: "", HoldLaterRings: false, TimeBasedOn: "LocalTime", Rings: rings, IsSeed: false);
        _h.Rings.Plans.Add(plan);
        return plan;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(13)]
    [InlineData(100)]
    public async Task ARunOfAnyRingCountListsEveryDeploymentAndSumsTheRequiredRings(int count)
    {
        var plan = PlanOf(count);
        var page = await _h.OpenAsync(DeploymentHelperViewModel.RingsTab);
        var rings = page.Rings;
        rings.SelectedObject = rings.Objects.Single(o => o.Object.Id == _h.SevenZip.Id);
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == plan.Name);
        await rings.ExpandCommand.ExecuteAsync(null);
        Assert.Equal(count, rings.PreviewRows.Count);
        await rings.ValidateCommand.ExecuteAsync(null);
        Assert.Equal("Every check passed.", rings.ValidationMessage);
        await rings.CreateRunCommand.ExecuteAsync(null);

        var shown = _h.Plans.Shown.Single();
        Assert.Equal(count, shown.Items.Count);
        var required = count - 1;
        if (required == 0) Assert.Null(shown.ExtraConfirmation);
        else if (required == 1) Assert.StartsWith("I understand that the Required ring Wave 2 installs software on every device of Wave 2 devices (10 devices now)", shown.ExtraConfirmation);
        else Assert.Equal($"I understand that {required} Required rings enforce on {required * 10} devices in total at their deadlines.", shown.ExtraConfirmation);
        Assert.Equal(count, _h.Plans.LastViewModel!.ResultRows.Count(r => r.Succeeded));
        Assert.Equal($"{count} created", rings.Runs.Single().Rings);
    }

    [Fact]
    public async Task AWideRunShowsAShortStatusLineAndTheFullStateOnSelection()
    {
        var statuses = Enumerable.Range(1, 13).Select(i => i <= 5 ? "Created" : i <= 7 ? "Creating" : "Held").ToArray();
        _h.Rings.AddRun("RUN-013", _h.SevenZip, statuses.Select((s, i) => ($"Wave {i + 1}", Id(i + 1), s, (int?)null, "Available")).ToArray());
        var page = await _h.OpenAsync(DeploymentHelperViewModel.RingsTab);
        var rings = page.Rings;
        Assert.Equal("5 created, 2 creating, 6 held", rings.Runs.Single().Rings);
        rings.SelectedRun = rings.Runs.Single();
        await rings.CountsTask;
        Assert.Equal(13, rings.RunRings.Count);
        Assert.Equal(statuses, rings.RunRings.Select(r => r.Status));
        Assert.Equal("Ring Wave 7 is creating, not created, so ring Wave 8 cannot be promoted. Select Refresh and reconcile.", rings.PromoteReason);
    }

    [Fact]
    public async Task UndoRunOfAHundredRingRunListsEveryRemovalAndAsksOnce()
    {
        _h.Rings.AddRun("RUN-100", _h.SevenZip, Enumerable.Range(1, 100).Select(i => ($"Wave {i}", Id(i), i <= 60 ? "Created" : "Held", (int?)null, "Available")).ToArray());
        var page = await _h.OpenAsync(DeploymentHelperViewModel.RingsTab);
        var rings = page.Rings;
        rings.SelectedRun = rings.Runs.Single();
        await rings.CountsTask;
        await rings.UndoRunCommand.ExecuteAsync(null);
        var plan = _h.Plans.Shown.Single();
        Assert.Equal(60, plan.Items.Count(i => i.Action == "Remove deployment"));
        Assert.Equal(40, plan.Items.Count(i => i.Action == "Cancel held ring"));
        Assert.Equal("Remove 60 deployments", plan.ActionName);
        Assert.Single(_h.Dialogs.RemovalPrompts);
    }

    [Fact]
    public async Task TheHistoryRunFilterListsEveryRunOfManyRings()
    {
        for (var run = 1; run <= 13; run++)
            _h.Audit.Records.Add(_h.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00010", $"{{DEP-{run}}}") with
            {
                Extra = [new("PlanName", "Wide-Rings"), new("RingIndex", "1"), new("RingName", "Wave 1"), new("RunId", $"RUN-{run:000}")]
            });
        var page = await _h.OpenAsync(DeploymentHelperViewModel.HistoryTab);
        Assert.Equal(14, page.History.Runs.Count);
        page.History.RunFilter = "RUN-013";
        Assert.Single(page.History.Rows);
    }

    // ---- Plan editor helpers -----------------------------------------------------------------------

    private static CollectionRow Row(string id, string name, int members, bool builtIn) => new(CollectionId: id, Name: name, FolderPath: "Rings", LimitingCollectionId: "SMS00001",
        LimitingCollectionName: "All Systems", MemberCount: members, IsBuiltIn: builtIn, RefreshType: "Periodic", Comment: "", DirectRuleCount: 0, QueryRuleCount: 0, IncludeRuleCount: 0, ExcludeRuleCount: 0);

    private static IReadOnlyList<CollectionRow> Collections(int count) =>
        Enumerable.Range(1, count).Select(i => Row(Id(i), $"Zone {(char)('A' + (count - i) % 26)} {i:000}", 10, false))
            .ToList();

    private (RingPlanEditorViewModel Editor, List<IReadOnlyList<CollectionRow>> Picks) Editor(IReadOnlyList<CollectionRow> collections, RingPlan? plan = null)
    {
        var picks = new List<IReadOnlyList<CollectionRow>>();
        var editor = new RingPlanEditorViewModel(plan, false, _h.Rings.Plans, collections, (_, _) => Task.FromResult(true),
            current => collections.FirstOrDefault(c => c.CollectionId == current), () => picks.Count > 0 ? picks[^1] : []);
        return (editor, picks);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(13)]
    [InlineData(100)]
    public void AddRingsFromCollectionsMakesOneRingPerCollectionWithGeneratedOffsets(int count)
    {
        var collections = Collections(count);
        var (editor, picks) = Editor(collections);
        editor.Name = "Zones";
        editor.Rings.Clear();
        editor.FirstOffsetText = "1";
        editor.DaysBetweenText = "2";
        picks.Add(collections);
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(count, editor.Rings.Count);
        Assert.Equal(collections.Select(c => c.Name), editor.Rings.Select(r => r.Name));
        Assert.Equal(Enumerable.Range(0, count).Select(i => (1 + 2 * i).ToString(CultureInfo.InvariantCulture)), editor.Rings.Select(r => r.AvailableOffsetText));
        Assert.Equal($"Added {count} ring{(count == 1 ? "" : "s")}.", editor.ValidationMessage);
        Assert.Equal($"Ring {count} of {count}", editor.RingPosition);
        Assert.NotNull(editor.Build(out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void AddRingsFromCollectionsCanSortByNameAndLeavesOutUsedAndBuiltInCollections()
    {
        var collections = Collections(13).Append(Row("SMS00001", "All Systems", 99, true)).ToList();
        var (editor, picks) = Editor(collections);
        editor.Rings.Clear();
        editor.SortNewRingsByName = true;
        picks.Add(collections.Take(3).Append(collections[^1]).ToList());
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(collections.Take(3).Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase), editor.Rings.Select(r => r.Name));
        Assert.Equal("Added 3 rings. 1 collection already in the plan or built in was left out.", editor.ValidationMessage);
        picks.Add(collections.Take(4).ToList());
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(4, editor.Rings.Count);
        Assert.Equal("21", editor.Rings[3].AvailableOffsetText);
    }

    [Fact]
    public void ThePlanEditorRefusesMoreRingsThanTheCapAndNamesIt()
    {
        var collections = Collections(100);
        var (editor, picks) = Editor(collections);
        picks.Add(collections);
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(1, editor.Rings.Count);
        Assert.StartsWith("A plan holds at most 100 rings.", editor.ValidationMessage);
        editor.Rings.Clear();
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(100, editor.Rings.Count);
        editor.AddRingCommand.Execute(null);
        Assert.Equal("A plan holds at most 100 rings.", editor.ValidationMessage);
        editor.SelectedRing = editor.Rings[56];
        editor.DuplicateRingCommand.Execute(null);
        Assert.Equal(100, editor.Rings.Count);
        Assert.Equal("Ring 57 of 100", editor.RingPosition);
        Assert.Equal(100, RingPlanLimits.MaxRings);
    }

    [Fact]
    public void FillOffsetsSetsEveryRingAndEachStaysEditable()
    {
        var plan = PlanOf(13);
        var (editor, _) = Editor(Collections(13), plan);
        editor.FirstOffsetText = "0";
        editor.DaysBetweenText = "3";
        editor.DeadlineDaysText = "4";
        editor.FillOffsetsCommand.Execute(null);
        Assert.Equal(Enumerable.Range(0, 13).Select(i => (3 * i).ToString(CultureInfo.InvariantCulture)), editor.Rings.Select(r => r.AvailableOffsetText));
        Assert.Equal("40", editor.Rings[12].DeadlineOffsetText);
        Assert.Equal("Filled the offsets of 13 rings.", editor.ValidationMessage);
        editor.Rings[5].AvailableOffsetText = "16";
        Assert.NotNull(editor.Build(out _));
        editor.DeadlineDaysText = "0";
        editor.FillOffsetsCommand.Execute(null);
        Assert.StartsWith("Enter the first offset and the days between rings", editor.ValidationMessage);
    }

    [Fact]
    public void ApplyToAllCopiesTheCheckedSettingsOfTheSelectedRingToEveryRing()
    {
        var plan = PlanOf(13);
        var (editor, _) = Editor(Collections(13), plan);
        var source = editor.Rings[4];
        source.Notification = DeploymentText.Notifications[1];
        source.OverrideServiceWindow = true;
        source.Rerun = RingPlanEditorViewModel.RerunChoices[2];
        editor.SelectedRing = source;
        editor.ApplyPurpose = false;
        editor.ApplyToAllCommand.Execute(null);
        Assert.All(editor.Rings, r => Assert.Equal(DeploymentText.Notifications[1], r.Notification));
        Assert.All(editor.Rings, r => Assert.True(r.OverrideServiceWindow));
        Assert.All(editor.Rings, r => Assert.Equal(RingPlanEditorViewModel.RerunChoices[2], r.Rerun));
        Assert.False(editor.Rings[0].IsRequired);

        editor.ApplyPurpose = true;
        editor.ApplyNotification = editor.ApplyOptions = false;
        editor.ApplyToAllCommand.Execute(null);
        Assert.All(editor.Rings, r => Assert.True(r.IsRequired));
        Assert.Equal("3", editor.Rings[0].DeadlineOffsetText);
        Assert.Equal("Applied the settings of ring Wave 5 to 13 rings.", editor.ValidationMessage);
    }

    [Fact]
    public void DuplicateAddsACopyWithoutACollectionAfterTheSelectedRing()
    {
        var (editor, _) = Editor(Collections(2), PlanOf(2));
        editor.SelectedRing = editor.Rings[0];
        editor.DuplicateRingCommand.Execute(null);
        Assert.Equal(["Wave 1", "Wave 1 copy", "Wave 2"], editor.Rings.Select(r => r.Name));
        Assert.Equal("", editor.Rings[1].CollectionId);
        Assert.Same(editor.Rings[1], editor.SelectedRing);
        Assert.Equal("Ring 2 of 3", editor.RingPosition);
    }

    [Fact]
    public void TheMultiSelectPickerReturnsTheCheckedCollectionsInTheOrderShown()
    {
        var collections = Collections(13).Append(Row("SMS00001", "All Systems", 99, true)).ToList();
        var picker = new RingCollectionPickerViewModel("Check", collections, null, multiSelect: true);
        Assert.Equal("Add rings", picker.PickText);
        picker.PickCommand.Execute(null);
        Assert.Equal(RingCollectionPickerViewModel.NoneChecked, picker.Message);
        picker.Filter = "Zone";
        picker.CheckShownCommand.Execute(null);
        Assert.Equal("13 of 14 collections, 13 checked", picker.Count);
        picker.Collections[2].IsChecked = false;
        picker.PickCommand.Execute(null);
        Assert.Equal(picker.Collections.Where(c => c.IsChecked).Select(c => c.CollectionId), picker.Results.Select(c => c.CollectionId));
        Assert.Equal(12, picker.Results.Count);
    }

    // ---- The writer is the only caller of the ring service write -------------------------------------

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "ApplicationPackagerUI"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src");
    }

    private static IReadOnlyList<string> UiFilesWith(Regex pattern)
    {
        var root = SourceRoot();
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(root, "ApplicationPackagerUI"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) && !f.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            .Where(f => pattern.IsMatch(MaintenanceWindowWriterTests.Code(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Fact]
    public void OnlyTheRingWriterCallsTheRingServiceWrite()
    {
        static string P(string file) => Path.Combine("ApplicationPackagerUI", "Services", file);
        var naming = UiFilesWith(new Regex(@"\bIRingService\b", RegexOptions.CultureInvariant));
        Assert.Equal([P("DeploymentHelperPrompts.cs"), P("RingRunPlan.cs"), Path.Combine("ApplicationPackagerUI", "ViewModels", "Pages", "DeploymentShared.cs")],
            naming.Order(StringComparer.OrdinalIgnoreCase).ToList());
        Assert.Matches(@"\bservice\s*\.\s*ApplyAsync\b", MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), P("RingRunPlan.cs")))));
        foreach (var file in UiFilesWith(new Regex(@"\bRings\b|\bRingWriter\b", RegexOptions.CultureInvariant)).Where(f => !f.EndsWith("RingRunPlan.cs", StringComparison.Ordinal)
                     && !f.EndsWith("Plan.cs", StringComparison.Ordinal)))
            Assert.DoesNotMatch(@"\.\s*ApplyAsync\b", MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), file))));
    }

    [Fact]
    public void NoTypeOfTheDesktopButTheWriterThePageServicesAndTheFallbackHoldsTheRingService()
    {
        var allowed = new[] { typeof(RingWriter), typeof(DeploymentPageServices) };
        var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = typeof(RingWriter).Assembly.GetTypes()
            .Where(t => t.GetFields(all).Any(f => typeof(IRingService).IsAssignableFrom(f.FieldType))
                || t.GetProperties(all).Any(p => typeof(IRingService).IsAssignableFrom(p.PropertyType)))
            .Select(t => { while (t.DeclaringType is { } outer) t = outer; return t; })
            .Where(t => !t.Name.StartsWith("<", StringComparison.Ordinal) && t.Namespace?.StartsWith("ApplicationPackagerUI", StringComparison.Ordinal) == true)
            .Distinct().ToList();
        Assert.Contains(typeof(RingWriter), holders);
        Assert.All(holders, t => Assert.Contains(t, allowed));
    }
}
