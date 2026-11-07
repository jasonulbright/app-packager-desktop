using System.Diagnostics;
using System.Globalization;
using System.Text;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using static ApplicationPackagerEngine.Tests.Collections.StubSitePowerShell;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>Parameter insertion, stale state, races and partial undo of the Collection and Compliance page.</summary>
public sealed class CollectionComplianceViewModelEdgeTests
{
    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        Assert.True(condition(), $"Timed out waiting for {description}.");
    }

    private static async Task<CollectionComplianceViewModel> SelectAsync(CollectionComplianceViewModel viewModel, string id)
    {
        viewModel.SelectedRow = viewModel.Rows.Single(r => r.CollectionId == id);
        viewModel.SetSelectedRows([viewModel.SelectedRow]);
        await viewModel.DetailLoad;
        return viewModel;
    }

    private static string Build(string expression, string value, string label = "Value")
    {
        var (wql, problem) = WqlParameters.Build(expression, [("{P}", label, value)]);
        Assert.Null(problem);
        return wql!;
    }

    private static string? ProblemOf(string expression, string value, string label = "Value") =>
        WqlParameters.Build(expression, [("{P}", label, value)]).Problem;

    /// <summary>
    /// Splits a WQL text as the SMS Provider reads it: the code outside quoted literals, and the
    /// value of each literal with its backslash escapes resolved.
    /// </summary>
    private static (string Code, List<string> Literals) Tokens(string wql)
    {
        var code = new StringBuilder();
        var literals = new List<string>();
        StringBuilder? literal = null;
        var quote = '\0';
        for (var i = 0; i < wql.Length; i++)
        {
            var c = wql[i];
            if (literal is not null)
            {
                if (c == '\\' && i + 1 < wql.Length) literal.Append(wql[++i]);
                else if (c == quote)
                {
                    literals.Add(literal.ToString());
                    literal = null;
                    code.Append(c);
                }
                else literal.Append(c);
                continue;
            }
            code.Append(c);
            if (c is '\'' or '"')
            {
                quote = c;
                literal = new StringBuilder();
            }
        }
        Assert.Null(literal);
        return (code.ToString(), literals);
    }

    [WindowFact]
    public void AQuotedValueEscapesQuotesAndBackslashesWithABackslash()
    {
        Assert.Equal(@"like '%O\'Brien%'", Build("like '%{P}%'", "O'Brien"));
        Assert.Equal("= \"a\\\"b\"", Build("= \"{P}\"", "a\"b"));
        Assert.Equal(@"= 'LAB\\Group'", Build("= '{P}'", @"LAB\Group"));
        Assert.Equal(@"= '\' or 1=1 or \''", Build("= '{P}'", "' or 1=1 or '"));
        Assert.Equal(@"= '\\\' or 1=1 --'", Build("= '{P}'", @"\' or 1=1 --"));
        Assert.Equal(@"Name = 'O\'Brien-PC'", "Name = " + WqlParameters.Literal("O'Brien-PC"));
    }

    [WindowTheory]
    [InlineData("' or 1=1 or '")]
    [InlineData(@"\' or 1=1 ; delete")]
    [InlineData(@"x\")]
    [InlineData(@"\\\'")]
    [InlineData("\"; select * from SMS_R_User where \"1\"=\"1")]
    [InlineData("a'b\"c\\d")]
    public void AHostileValueStaysInsideItsLiteralThroughTheRealGuard(string value)
    {
        const string template = "select SMS_R_System.ResourceId from SMS_R_System where SMS_R_System.Name = '{P}' and SMS_R_System.Domain = \"{P}\"";
        var wql = Build(template, value);
        Assert.Null(WqlGuard.Check(wql));
        var (code, literals) = Tokens(wql);
        Assert.Equal([value, value], literals);
        Assert.Equal(Tokens(template.Replace("{P}", "", StringComparison.Ordinal)).Code, code);
    }

    [WindowFact]
    public void ABarePlaceholderTakesAWholeNumberOnly()
    {
        const string template = "select * from SMS_R_System where DATEDIFF(day,LastHardwareScan,GetDate()) > {P})";
        Assert.EndsWith("> 14)", Build(template, " 14 "));
        Assert.Equal(WqlParameters.WholeNumber("Days"), ProblemOf(template, "14 or 1=1", "Days"));
        Assert.Equal(WqlParameters.WholeNumber("Days"), ProblemOf(template, "'14'", "Days"));
        Assert.Equal(WqlParameters.WholeNumber("Days"), ProblemOf(template, "-1", "Days"));
        Assert.Equal(WqlParameters.WholeNumber("Days"), ProblemOf(template, "1.5", "Days"));
        Assert.Equal(WqlParameters.WholeNumber("Days"), ProblemOf(template, "99999999999999999999", "Days"));
    }

    [WindowFact]
    public void ABarePlaceholderInAnInListTakesWholeNumbersSeparatedByCommas()
    {
        const string template = "select * from SMS_G_System_SYSTEM_ENCLOSURE where ChassisTypes in ({P})";
        Assert.EndsWith("in ('8','9','10')", Build(template, "'8','9', 10"));
        Assert.Equal(WqlParameters.WholeNumbers("Chassis"), ProblemOf(template, "8) or (1=1", "Chassis"));
        Assert.Equal(WqlParameters.WholeNumbers("Chassis"), ProblemOf(template, "8,,9", "Chassis"));
        Assert.Equal(WqlParameters.WholeNumbers("Chassis"), ProblemOf(template, "'8", "Chassis"));
    }

    [WindowTheory]
    [InlineData('%')]
    [InlineData('_')]
    [InlineData('[')]
    public void ALikeValueRefusesAWildcardCharacterAndNamesIt(char wildcard)
    {
        Assert.Equal(WqlParameters.Wildcard("Software name", wildcard), ProblemOf("where DisplayName like '%{P}%'", $"7{wildcard}Zip", "Software name"));
        Assert.Equal(WqlParameters.Wildcard("Software name", wildcard), ProblemOf("where DisplayName LIKE\t\"{P}%\"", $"{wildcard}", "Software name"));
        Assert.Contains(wildcard.ToString(), WqlParameters.Wildcard("Software name", wildcard));
        Assert.EndsWith($"= '7{wildcard}Zip'", Build("where DisplayName = '{P}'", $"7{wildcard}Zip"));
    }

    [WindowFact]
    public void ControlCharactersAndLongValuesAreRefused()
    {
        Assert.Equal(WqlParameters.ControlCharacter, ProblemOf("= '{P}'", "a\nb"));
        Assert.Equal(WqlParameters.ControlCharacter, ProblemOf("= '{P}'", "a\u2028b"));
        Assert.Equal(WqlParameters.TooLong, ProblemOf("= '{P}'", new string('x', WqlParameters.MaxLength + 1)));
        Assert.Equal(WqlParameters.Required, ProblemOf("= {P}", " "));
    }

    [WindowFact]
    public void EveryShippedParameterizedTemplateBuildsAQueryTheGuardAccepts()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-cc-none-" + Guid.NewGuid().ToString("N"));
        var templates = new QueryTemplateStore(folder).All().Where(t => t.Parameters.Count > 0).ToList();
        Assert.NotEmpty(templates);
        foreach (var template in templates)
        {
            var (wql, problem) = WqlParameters.Build(template.Expression, template.Parameters.Select(p => (p, WqlParameters.Label(p), "1")));
            Assert.True(problem is null, template.Name + ": " + problem);
            Assert.True(WqlGuard.Check(wql) is null, template.Name + ": " + WqlGuard.Check(wql));
            Assert.DoesNotContain("{", wql);
        }
        var days = templates.Single(t => t.Name == "Hardware Inventory Not Reporting N Days");
        var built = WqlParameters.Build(days.Expression, [(days.Parameters[0], "Days", "14")]).Wql!;
        Assert.Contains("> 14", built);
        Assert.DoesNotContain("'14'", built);
        var chassis = templates.Single(t => t.Name == "Chassis Type");
        Assert.Contains("in ('8','9')", WqlParameters.Build(chassis.Expression, [(chassis.Parameters[0], "Chassis", "'8','9'")]).Wql!);
        Assert.Equal(WqlParameters.WholeNumber("Days"), WqlParameters.Build(days.Expression, [(days.Parameters[0], "Days", "14)) or (1=1")]).Problem);
    }

    [WindowFact]
    public async Task AGeneratedQueryReachesTheRealScriptUnchanged()
    {
        if (!WindowsPowerShellPresent) return;
        var wql = Build("select * from SMS_R_System where Name = '{P}'", @"O'Brien\PC");
        var site = new StubSitePowerShell(State(new { Reject = "xx", Fail = Array.Empty<string>() },
            Collection("MCM00010", "Workstations", "SMS00001"), Collection("SMS00001", "All Systems", "")));
        var service = new CollectionService(site);
        Assert.True((await service.PreviewWqlAsync(wql, 5, CancellationToken.None)).IsValid);
        var request = new CollectionChange(CollectionChangeKind.AddRule, "MCM00010", "Workstations", null, null, null,
            new CollectionRule(CollectionRuleKind.Query, "Named", wql, "", "", ""), null, null, []);
        var prepared = await service.PrepareAsync(request, CancellationToken.None);
        var outcome = Assert.Single(await service.ApplyAsync([prepared], new CollectionServiceTests.Recorder<CollectionOutcome>(), CancellationToken.None));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains(site.QueryRules("MCM00010"), r => r.Name == "Named" && r.Expression == wql);
    }

    [WindowFact]
    public async Task TheTemplatesTabRefusesANonNumberForABarePlaceholderWithAPlainMessage()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "OS build");
        viewModel.TemplateParameters[0].Value = "10.0.22631";
        Assert.Equal(WqlParameters.WholeNumber("Build"), viewModel.TemplateMessage);
        Assert.False(viewModel.ApplyTemplateCommand.CanExecute(null));
        viewModel.TemplateParameters[0].Value = "22631";
        Assert.EndsWith("SMS_R_System.Build = 22631", viewModel.TemplateWql);
        Assert.True(viewModel.ApplyTemplateCommand.CanExecute(null));
    }

    [WindowFact]
    public async Task ACancelledDialogAfterAnEarlierWriteDoesNotReadTheListAgain()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.SetSelectedRows(viewModel.Rows.Where(r => r.CollectionId == "LAB00010"));
        await viewModel.EvaluateCommand.ExecuteAsync(null);
        var lists = harness.Service.ListCalls;
        viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "All workstations");
        harness.Prompts.Editor = _ => { };
        harness.Prompts.Picker = _ => { };
        harness.Prompts.Prompt = _ => { };
        await viewModel.NewCollectionCommand.ExecuteAsync(null);
        await viewModel.ApplyTemplateCommand.ExecuteAsync(null);
        await SelectAsync(viewModel, "LAB00011");
        await viewModel.AddDirectMembersCommand.ExecuteAsync(null);
        await viewModel.AddIncludeRuleCommand.ExecuteAsync(null);
        viewModel.WqlText = "select * from SMS_R_System";
        await viewModel.SaveQueryAsNewCommand.ExecuteAsync(null);
        Assert.Equal(lists, harness.Service.ListCalls);
    }

    [WindowFact]
    public async Task AfterOpenByIdTheWritesThatNeedTheWholeListAskForARefresh()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = harness.Create();
        viewModel.OpenByIdText = "LAB00011";
        await viewModel.OpenByIdCommand.ExecuteAsync(null);
        await viewModel.DetailLoad;
        viewModel.SelectedTemplate = null;
        harness.Prompts.Editor = _ => Assert.Fail("The editor opened with one collection in the list.");
        harness.Prompts.Picker = _ => Assert.Fail("The picker opened with one collection in the list.");

        await viewModel.NewCollectionCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.PartialListRefused, viewModel.StatusMessage);
        viewModel.StatusMessage = "";
        await viewModel.EditCollectionCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.PartialListRefused, viewModel.StatusMessage);
        viewModel.StatusMessage = "";
        await viewModel.CopyCollectionCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.PartialListRefused, viewModel.StatusMessage);
        viewModel.StatusMessage = "";
        await viewModel.AddExcludeRuleCommand.ExecuteAsync(null);
        Assert.Equal(CollectionComplianceViewModel.PartialListRefused, viewModel.StatusMessage);
        Assert.Equal(0, harness.Prompts.EditorShows + harness.Prompts.PickerShows);
    }

    [WindowFact]
    public async Task LeavingThePageReleasesTheResultsTheUndoAndTheEditorText()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.WqlCollection = viewModel.Rows.Single(r => r.CollectionId == "LAB00011");
        await viewModel.WqlRulesLoad;
        viewModel.WqlText = "select SMS_R_System.ResourceId, SMS_R_System.Name from SMS_R_System";
        viewModel.WqlRuleName = "All devices";
        await viewModel.PreviewWqlCommand.ExecuteAsync(null);
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasUndo);
        Assert.NotEmpty(viewModel.LastResults);

        await viewModel.OnNavigatedFromAsync();
        Assert.False(viewModel.HasUndo);
        Assert.False(viewModel.UndoLastCommand.CanExecute(null));
        Assert.Empty(viewModel.LastResults);
        Assert.False(viewModel.HasResults);
        Assert.Equal("", viewModel.ResultSummary);
        Assert.Equal("", viewModel.WqlText);
        Assert.Equal("", viewModel.WqlRuleName);
        Assert.Equal("", viewModel.WqlMessage);
        Assert.Empty(viewModel.PreviewRows);

        await viewModel.OnNavigatedToAsync();
        viewModel.WqlCollection = viewModel.Rows.Single(r => r.CollectionId == "LAB00011");
        await viewModel.WqlRulesLoad;
        viewModel.WqlText = "select SMS_R_System.ResourceId, SMS_R_System.Name from SMS_R_System";
        viewModel.WqlRuleName = "All devices 2";
        await viewModel.AddRuleCommand.ExecuteAsync(null);
        Assert.Contains(CollectionComplianceViewModel.NoPreviewNote, harness.Plans.Shown[^1].Items[0].Detail);
    }

    [WindowFact]
    public async Task AWriteThatEndsAfterThePageWasLeftDoesNotFillTheList()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        harness.Prompts.Editor = e =>
        {
            e.Name = "CCM-Test-9";
            FakeCollectionPrompts.Save(e);
            ScriptedPlanDialogs.Wait(viewModel.OnNavigatedFromAsync());
        };
        await viewModel.NewCollectionCommand.ExecuteAsync(null);
        Assert.Single(harness.Service.Applied);
        Assert.Equal(1, harness.Service.ListCalls);
        Assert.Empty(viewModel.Rows);
        Assert.False(viewModel.IsDataLoaded);
        Assert.False(viewModel.HasUndo);
    }

    [WindowFact]
    public async Task MemberPagingKeepsThePageUntilTheNextArrivesAndIsOffWhileItLoads()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.MemberPageSize = 1;
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00010");
        Assert.Equal("Members 1 to 1 of 3", viewModel.MemberPageText);
        harness.Service.DetailGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = viewModel.NextMemberPageCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsDetailLoading);
        Assert.False(viewModel.NextMemberPageCommand.CanExecute(null));
        Assert.False(viewModel.PreviousMemberPageCommand.CanExecute(null));
        Assert.Equal(0, viewModel.MemberPage);
        Assert.Single(viewModel.Members);
        Assert.Equal("Members 1 to 1 of 3", viewModel.MemberPageText);
        harness.Service.DetailGate.SetResult();
        harness.Service.DetailGate = null;
        await next;
        Assert.Equal(1, viewModel.MemberPage);
        Assert.Equal("Members 2 to 2 of 3", viewModel.MemberPageText);
        Assert.True(viewModel.NextMemberPageCommand.CanExecute(null));
        Assert.True(viewModel.PreviousMemberPageCommand.CanExecute(null));

        lock (harness.Service.Collections) harness.Service.Collections.RemoveAll(c => c.CollectionId == "LAB00010");
        await viewModel.NextMemberPageCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.MemberPage);
        Assert.Equal("Servers-PC02", Assert.Single(viewModel.Members).Name);
        Assert.Equal("Members 2 to 2 of 3", viewModel.MemberPageText);
        Assert.NotEqual("", viewModel.DetailMessage);
        Assert.True(viewModel.NextMemberPageCommand.CanExecute(null));
    }

    [WindowFact]
    public async Task ADeviceNameThatMatchesSeveralDevicesIsSkippedAndAResourceIdAddsOne()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.Devices.Add(("16777301", "DUP"));
        harness.Service.Devices.Add(("16777302", "DUP"));
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00011");
        harness.Prompts.Prompt = p =>
        {
            p.Text = "DUP\r\nPC01";
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.AddDirectMembersCommand.ExecuteAsync(null);
        var plan = harness.Plans.Shown[^1];
        var reason = CollectionComplianceViewModel.AmbiguousDevice(["16777301", "16777302"]);
        Assert.Equal(["Skip|DUP|" + reason, "Add|Workstations (LAB00011)|Direct member PC01 (16777201)."], plan.Items.Select(i => $"{i.Action}|{i.Target}|{i.Detail}"));
        Assert.DoesNotContain(harness.Service.Rules["LAB00011"], r => r.RuleName == "DUP");

        harness.Prompts.Prompt = p =>
        {
            p.Text = "16777302";
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.AddDirectMembersCommand.ExecuteAsync(null);
        Assert.Contains("SMS_R_System.ResourceId = 16777302", harness.Service.PreviewExpressions[^1]);
        Assert.Equal("Add|Workstations (LAB00011)|Direct member DUP (16777302).", harness.Plans.Shown[^1].Items.Select(i => $"{i.Action}|{i.Target}|{i.Detail}").Single());
        Assert.Contains(harness.Service.Rules["LAB00011"], r => r.ResourceId == "16777302");
    }

    private static string IntervalToken(int minutes = 0, int hours = 0, int days = 0) =>
        ((2UL << 19) | ((ulong)minutes << 13) | ((ulong)hours << 8) | ((ulong)days << 3) | (1UL << 44) | (1UL << 48) | (30UL << 38)).ToString("X16", CultureInfo.InvariantCulture);

    [WindowFact]
    public void TheRefreshIntervalIsReadFromTheScheduleToken()
    {
        Assert.Equal(new RefreshInterval(7, "Days"), CollectionsText.IntervalOf(IntervalToken(days: 7)));
        Assert.Equal(new RefreshInterval(2, "Hours"), CollectionsText.IntervalOf(IntervalToken(hours: 2)));
        Assert.Equal(new RefreshInterval(30, "Minutes"), CollectionsText.IntervalOf(IntervalToken(minutes: 30)));
        Assert.Null(CollectionsText.IntervalOf(""));
        Assert.Null(CollectionsText.IntervalOf("zz"));
        Assert.Null(CollectionsText.IntervalOf(IntervalToken(hours: 1, days: 1)));
        Assert.Equal("every 1 day", CollectionsText.Every(new RefreshInterval(1, "Days")));
        Assert.Equal("every 12 hours", CollectionsText.Every(new RefreshInterval(12, "Hours")));
    }

    [WindowFact]
    public void TheEditorValidatesTheRefreshIntervalForPeriodicRefreshOnly()
    {
        var harness = new CollectionComplianceHarness();
        var editor = new CollectionEditorViewModel(CollectionEditorMode.New, harness.Service.Collections, _ => Task.FromResult(true));
        editor.Name = "CCM-Test-10";
        editor.RefreshType = "Periodic";
        Assert.True(editor.IsRefreshIntervalVisible);
        Assert.Equal(["Minutes", "Hours", "Days"], CollectionEditorViewModel.RefreshUnits);
        Assert.Equal("", editor.RefreshCount);
        Assert.Null(editor.Validate());
        Assert.Null(editor.BuildRow()!.RefreshInterval);

        editor.RefreshUnit = "Minutes";
        foreach (var bad in new[] { "0", "60", "abc", "1.5", "-3" })
        {
            editor.RefreshCount = bad;
            Assert.Equal(CollectionEditorViewModel.RefreshIntervalInvalid, editor.Validate());
        }
        editor.RefreshCount = "59";
        Assert.Equal(new RefreshInterval(59, "Minutes"), editor.BuildRow()!.RefreshInterval);
        editor.RefreshUnit = "Hours";
        Assert.Equal(CollectionEditorViewModel.RefreshIntervalInvalid, editor.Validate());
        editor.RefreshCount = "23";
        Assert.Null(editor.Validate());
        editor.RefreshUnit = "Days";
        editor.RefreshCount = "32";
        Assert.Equal(CollectionEditorViewModel.RefreshIntervalInvalid, editor.Validate());
        editor.RefreshCount = "31";
        Assert.Equal(new RefreshInterval(31, "Days"), editor.BuildRow()!.RefreshInterval);

        editor.RefreshType = "Continuous";
        Assert.False(editor.IsRefreshIntervalVisible);
        editor.RefreshCount = "abc";
        Assert.Null(editor.Validate());
        Assert.Null(editor.BuildRow()!.RefreshInterval);
    }

    [WindowFact]
    public async Task TheCreatePlanShowsTheIntervalAndTheDefaultWhenNoneWasGiven()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        harness.Prompts.Editor = e =>
        {
            e.Name = "CCM-Test-11";
            e.RefreshType = "Both";
            e.RefreshCount = "2";
            e.RefreshUnit = "Hours";
            FakeCollectionPrompts.Save(e);
        };
        await viewModel.NewCollectionCommand.ExecuteAsync(null);
        Assert.Contains("Refresh: Both, every 2 hours.", harness.Plans.Shown[^1].Items[0].Detail);
        Assert.Equal(new RefreshInterval(2, "Hours"), harness.Service.Applied[^1][0].AfterRow!.RefreshInterval);

        harness.Prompts.Editor = e =>
        {
            e.Name = "CCM-Test-12";
            e.RefreshType = "Periodic";
            FakeCollectionPrompts.Save(e);
        };
        await viewModel.NewCollectionCommand.ExecuteAsync(null);
        Assert.Contains("Refresh: Periodic, " + CollectionComplianceViewModel.DefaultIntervalText + ".", harness.Plans.Shown[^1].Items[0].Detail);
        Assert.Null(harness.Service.Applied[^1][0].AfterRow!.RefreshInterval);

        harness.Prompts.Editor = e =>
        {
            e.Name = "CCM-Test-13";
            e.RefreshType = "Manual";
            FakeCollectionPrompts.Save(e);
        };
        await viewModel.NewCollectionCommand.ExecuteAsync(null);
        Assert.Contains("Refresh: Manual.", harness.Plans.Shown[^1].Items[0].Detail);
    }

    [WindowFact]
    public async Task TheEditPlanShowsTheOldAndNewIntervalAndAnUnchangedOneIsNotSent()
    {
        var harness = new CollectionComplianceHarness();
        lock (harness.Service.Collections)
        {
            var index = harness.Service.Collections.FindIndex(c => c.CollectionId == "LAB00010");
            harness.Service.Collections[index] = harness.Service.Collections[index] with { RefreshSchedule = IntervalToken(days: 1) };
        }
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00010");
        harness.Prompts.Editor = e =>
        {
            Assert.Equal(("1", "Days"), (e.RefreshCount, e.RefreshUnit));
            e.RefreshCount = "12";
            e.RefreshUnit = "Hours";
            FakeCollectionPrompts.Save(e);
        };
        await viewModel.EditCollectionCommand.ExecuteAsync(null);
        var item = Assert.Single(harness.Plans.Shown[^1].Items);
        Assert.Contains("Refresh interval: every 1 day to every 12 hours", item.Detail);
        Assert.Equal(new RefreshInterval(12, "Hours"), harness.Service.Applied[^1][0].AfterRow!.RefreshInterval);

        await SelectAsync(viewModel, "LAB00010");
        harness.Prompts.Editor = e =>
        {
            Assert.Equal(("1", "Days"), (e.RefreshCount, e.RefreshUnit));
            e.Comment = "Changed";
            FakeCollectionPrompts.Save(e);
        };
        lock (harness.Service.Collections)
        {
            var index = harness.Service.Collections.FindIndex(c => c.CollectionId == "LAB00010");
            harness.Service.Collections[index] = harness.Service.Collections[index] with { RefreshSchedule = IntervalToken(days: 1), RefreshInterval = null };
        }
        await viewModel.RefreshCommand.ExecuteAsync(null);
        await SelectAsync(viewModel, "LAB00010");
        await viewModel.EditCollectionCommand.ExecuteAsync(null);
        var unchanged = harness.Plans.Shown[^1].Items[0].Detail!;
        Assert.DoesNotContain("Refresh interval:", unchanged);
        Assert.Null(harness.Service.Applied[^1][0].AfterRow!.RefreshInterval);
    }

    [WindowFact]
    public async Task ARefreshReadsTheDetailOfTheSelectedCollectionAgain()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await SelectAsync(await harness.OpenAsync(), "LAB00010");
        lock (harness.Service.Collections)
        {
            var index = harness.Service.Collections.FindIndex(c => c.CollectionId == "LAB00010");
            harness.Service.Collections[index] = harness.Service.Collections[index] with { Comment = "Patched monthly" };
        }
        await viewModel.RefreshCommand.ExecuteAsync(null);
        await viewModel.DetailLoad;
        Assert.Contains(viewModel.Properties, p => p.Name == "Comment" && p.Value == "Patched monthly");
    }

    [WindowFact]
    public async Task TheWqlEditorFollowsTheRefreshedCollectionAndDropsARemovedOne()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.WqlCollection = viewModel.Rows.Single(r => r.CollectionId == "LAB00010");
        await viewModel.WqlRulesLoad;
        lock (harness.Service.Collections)
        {
            var index = harness.Service.Collections.FindIndex(c => c.CollectionId == "LAB00010");
            harness.Service.Collections[index] = harness.Service.Collections[index] with { Name = "Servers renamed" };
        }
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Servers renamed (LAB00010)", viewModel.WqlCollectionText);

        lock (harness.Service.Collections) harness.Service.Collections.RemoveAll(c => c.CollectionId == "LAB00010");
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Null(viewModel.WqlCollection);
        Assert.Empty(viewModel.WqlRules);
        Assert.Equal(CollectionComplianceViewModel.WqlCollectionGone("Servers renamed (LAB00010)"), viewModel.WqlMessage);
    }

    [WindowFact]
    public async Task AFullLoadThatEndsAfterOpenByIdDoesNotReplaceTheOpenedCollection()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = harness.Create();
        var visit = viewModel.OnNavigatedToAsync();
        await WaitForAsync(() => Volatile.Read(ref harness.Service.ListCalls) != 0, "the collection load to start");
        viewModel.OpenByIdText = "LAB00011";
        await viewModel.OpenByIdCommand.ExecuteAsync(null);
        harness.Service.ListGate.SetResult();
        await visit;
        Assert.Equal(["Workstations"], viewModel.Rows.Select(r => r.Name));
        Assert.Equal(CollectionComplianceViewModel.OpenedById, viewModel.RowCount);
        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.RefreshCommand.CanExecute(null));
    }

    [WindowFact]
    public async Task AnOpenByIdThatEndsAfterARefreshStartedIsDiscarded()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        harness.Service.Add("LAB00020", "Late");
        harness.Service.GetGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.OpenByIdText = "LAB00020";
        var open = viewModel.OpenByIdCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        harness.Service.GetGate.SetResult();
        await open;
        Assert.Equal(5, viewModel.Rows.Count);
        Assert.Equal("5 collections", viewModel.RowCount);
    }

    [WindowFact]
    public async Task ConnectSiteAndOpenByIdThatEndAfterLeavingThePageChangeNothing()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        harness.Service.Add("LAB00020", "Late");
        harness.Service.GetGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = viewModel.ConnectSiteCommand.ExecuteAsync(null);
        viewModel.OpenByIdText = "LAB00020";
        var open = viewModel.OpenByIdCommand.ExecuteAsync(null);
        await viewModel.OnNavigatedFromAsync();
        harness.Service.GetGate.SetResult();
        await Task.WhenAll(connect, open);
        Assert.Equal("", viewModel.StatusMessage);
        Assert.Equal("", viewModel.ConnectionMessage);
        Assert.Empty(viewModel.Rows);
        Assert.Null(viewModel.SelectedRow);
    }

    [WindowFact]
    public async Task APartialUndoKeepsTheUndoOfTheChangesThatAreStillMade()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "All workstations");
        harness.Prompts.Picker = p =>
        {
            foreach (var i in p.Items) i.IsChecked = true;
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.ApplyTemplateCommand.ExecuteAsync(null);
        Assert.All(new[] { "LAB00010", "LAB00011", "LAB00012" }, id => Assert.Contains(harness.Service.Rules[id], r => r.RuleName == "All workstations"));

        harness.Service.Fail = c => c.Kind == CollectionChangeKind.RemoveRule && c.CollectionId == "LAB00011" ? "Access is denied for Workstations." : null;
        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal(3, harness.Plans.Shown[^1].Items.Count);
        Assert.True(viewModel.HasUndo);

        harness.Service.Fail = null;
        await viewModel.UndoLastCommand.ExecuteAsync(null);
        var retry = Assert.Single(harness.Plans.Shown[^1].Items);
        Assert.Equal("Workstations (LAB00011)", retry.Target);
        Assert.All(new[] { "LAB00010", "LAB00011", "LAB00012" }, id => Assert.DoesNotContain(harness.Service.Rules[id], r => r.RuleName == "All workstations"));
        Assert.False(viewModel.HasUndo);
    }

    [WindowFact]
    public async Task APartialUndoInThePlanWindowKeepsTheUndoOfTheRest()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "All workstations");
        harness.Plans.RunUndo = true;
        harness.Service.Fail = c => c.Kind == CollectionChangeKind.RemoveRule && c.CollectionId == "LAB00012" ? "Access is denied for Pilot." : null;
        harness.Prompts.Picker = p =>
        {
            foreach (var i in p.Items) i.IsChecked = true;
            FakeCollectionPrompts.Accept(p);
        };
        await viewModel.ApplyTemplateCommand.ExecuteAsync(null);
        Assert.Contains(harness.Service.Rules["LAB00012"], r => r.RuleName == "All workstations");
        Assert.True(viewModel.HasUndo);

        harness.Plans.RunUndo = false;
        harness.Service.Fail = null;
        await viewModel.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal("Pilot (LAB00012)", Assert.Single(harness.Plans.Shown[^1].Items).Target);
        Assert.DoesNotContain(harness.Service.Rules["LAB00012"], r => r.RuleName == "All workstations");
        Assert.False(viewModel.HasUndo);
    }
}
