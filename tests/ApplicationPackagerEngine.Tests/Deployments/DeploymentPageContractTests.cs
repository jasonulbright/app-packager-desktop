using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The page against the service contract: the second check box, the create-now box, the options ConfigMgr accepts and the history records.</summary>
public sealed class DeploymentPageContractTests
{
    private readonly DeploymentPageHarness _h = new();

    private async Task<DeploymentFormViewModel> FormAsync(DeployableObject? obj = null)
    {
        var page = await _h.OpenAsync();
        await DeploymentPageHarness.PickAsync(page.Deploy, obj ?? _h.SevenZip, "LAB00010");
        return page.Deploy;
    }

    private static void SetRequired(DeploymentFormViewModel form)
    {
        form.IsRequired = true;
        form.DeadlineDate = form.AvailableDate!.Value.AddDays(1);
        form.DeadlineTimeText = "18:00";
    }

    // ---- The second check box -------------------------------------------------------------------

    [Fact]
    public async Task TheSecondBoxNamesOnlyTheRequiredConditionWhenTheTimeIsNotBackdated()
    {
        var form = await FormAsync();
        SetRequired(form);
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal("I understand that this Required deployment installs 7-Zip on every device of Pilot Workstations (3 devices now) at the deadline 2026-10-21 18:00 local time.",
            _h.Plans.Shown.Single().ExtraConfirmation);
    }

    [Fact]
    public async Task TheSecondBoxNamesOnlyTheBackdatedConditionForAnAvailableDeployment()
    {
        var form = await FormAsync();
        form.AvailableTimeText = "07:00";
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal("I understand that the available time 2026-10-20 07:00 local time is in the past, so the deployment is available at once.",
            _h.Plans.Shown.Single().ExtraConfirmation);
    }

    [Fact]
    public async Task TheSecondBoxNamesBothConditionsInOneSentenceWhenBothApply()
    {
        var form = await FormAsync();
        SetRequired(form);
        form.AvailableTimeText = "07:00";
        form.OverrideServiceWindow = true;
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Equal("I understand that this Required deployment installs 7-Zip on every device of Pilot Workstations (3 devices now) at the deadline 2026-10-21 18:00 local time "
            + "and installs outside maintenance windows, and that the available time 2026-10-20 07:00 local time is in the past, so the deployment is available at once.",
            _h.Plans.Shown.Single().ExtraConfirmation);
        var sent = _h.Service.Applied.Single().Single();
        Assert.True(sent.RequiredConfirmed && sent.BackdatedAcknowledged);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, true)]
    public void TheWriterSetsEachFlagOnlyForWhatThePlanAsked(bool acknowledged, bool backdated, bool expectRequired, bool expectBackdated)
    {
        var required = Change(DeploymentChangeKind.Create, "Required");
        var (send, held) = DeploymentWriter.Confirmed([required], acknowledged, backdated, createNow: false);
        Assert.Equal(acknowledged ? 0 : 1, held.Count);
        if (acknowledged) Assert.Equal((expectRequired, expectBackdated), (send.Single().RequiredConfirmed, send.Single().BackdatedAcknowledged));
        else Assert.Empty(send);
    }

    // ---- The create-now box ---------------------------------------------------------------------

    [Fact]
    public async Task ACombinedPlanCarriesTheCreateNowBoxAndOnlyAChosenBoxReachesTheService()
    {
        var form = await FormAsync(_h.Notepad);
        form.DpGroups[0].IsChecked = true;
        form.DistributeFirst = true;
        await form.DeployCommand.ExecuteAsync(null);

        var plan = _h.Plans.Shown.Single();
        Assert.Equal("Create the deployment now, while the content is still distributing.", plan.OptionalChoice);
        Assert.Contains(DeploymentText.CreateNowHint, plan.Items.Single().Detail);
        Assert.Null(plan.ExtraConfirmation);
        var first = _h.Service.Applied.Single().Single();
        Assert.Equal(["All DPs"], first.DpGroups);
        Assert.False(first.CreateNowContentDistributing);

        _h.Service.Deployments.Clear();
        _h.Plans.ChooseOption = true;
        await form.DeployCommand.ExecuteAsync(null);
        Assert.True(_h.Service.Applied[1].Single().CreateNowContentDistributing);
    }

    [Fact]
    public async Task APlanWithoutADistributionHasNoCreateNowBoxAndCheckedGroupsDoNotTravelWithTheCreate()
    {
        var form = await FormAsync();
        form.DpGroups[0].IsChecked = true;
        _h.Plans.ChooseOption = true;
        await form.DeployCommand.ExecuteAsync(null);
        Assert.Null(_h.Plans.Shown.Single().OptionalChoice);
        var sent = _h.Service.Applied.Single().Single();
        Assert.Empty(sent.DpGroups);
        Assert.False(sent.CreateNowContentDistributing);
    }

    [Fact]
    public void TheFormHasNoCreateNowInput()
    {
        Assert.Null(typeof(DeploymentFormViewModel).GetProperty("CreateNowContentDistributing"));
        var xaml = File.ReadAllText(Path.Combine(SourceRoot(), "ApplicationPackagerUI", "Views", "Pages", "DeploymentHelperPage.xaml"));
        Assert.DoesNotContain("chkCreateNow", xaml);
        var window = File.ReadAllText(Path.Combine(SourceRoot(), "ApplicationPackagerUI", "Views", "Windows", "ChangePlanWindow.xaml"));
        Assert.Contains("chkChangePlanOption", window);
    }

    [Fact]
    public async Task TheOptionBoxOfThePlanWindowIsOffUntilTheOperatorSelectsIt()
    {
        var plan = new ChangePlan("Deploy", "Distribute and deploy", [new ChangePlanItem("Distribute and create deployment", "x", null)]) { OptionalChoice = DeploymentText.CreateNowChoice };
        ConfirmedChangePlan? seen = null;
        var dialog = new ChangePlanViewModel(plan, (c, _, _) => { seen = c; return Task.FromResult(new ChangePlanResult([])); });
        Assert.True(dialog.HasOptionalChoice);
        Assert.Equal(DeploymentText.CreateNowChoice, dialog.OptionalChoiceText);
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.False(seen!.OptionChosen);

        var chosen = new ChangePlanViewModel(plan, (c, _, _) => { seen = c; return Task.FromResult(new ChangePlanResult([])); }) { OptionChosen = true };
        await chosen.ConfirmCommand.ExecuteAsync(null);
        Assert.True(seen!.OptionChosen);
        Assert.Equal(DeploymentText.CreateNowChoice, seen.Plan.OptionalChoice);
    }

    // ---- Options ConfigMgr accepts --------------------------------------------------------------

    [Fact]
    public void TheChoiceListsOfferOnlyValuesConfigMgrStores()
    {
        Assert.Equal(["DoNotRunProgram", "DownloadContentFromDistributionPointAndLocally"], DeploymentText.SlowNetworkBehaviors.Select(c => c.Value));
        Assert.Equal(["DownloadContentFromDistributionPointAndRunLocally", "RunProgramFromDistributionPoint"], DeploymentText.NetworkBehaviors.Select(c => c.Value));
        Assert.Equal(["DownloadContentLocallyWhenNeededByRunningTaskSequence", "DownloadAllContentLocallyBeforeStartingTaskSequence"], DeploymentText.TaskSequenceContents.Select(c => c.Value));
        Assert.Equal(["Clients", "ClientsMediaAndPxe", "MediaAndPxe", "MediaAndPxeHidden"], DeploymentText.TaskSequenceAvailabilities.Select(c => c.Value));
        Assert.All(DeploymentText.SlowNetworkBehaviors, c => Assert.Contains(c.Value, DeploymentScheduleRules.SlowNetwork));
        Assert.All(DeploymentText.TaskSequenceContents, c => Assert.Contains(c.Value, DeploymentScheduleRules.TaskSequenceOptions));
        Assert.Contains("suppress", DeploymentText.SuppressRestartLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not restart", DeploymentText.SuppressRestartLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheTypeOptionsReachTheRequestWithTheirOwnValues()
    {
        var form = await FormAsync(_h.Office);
        form.SlowNetworkBehavior = DeploymentText.SlowNetworkBehaviors[1];
        form.AllowFallback = true;
        Assert.Equal("DownloadContentFromDistributionPointAndLocally", form.BuildCreateRequest()!.Options!.SlowNetworkBehavior);
        Assert.True(form.BuildCreateRequest()!.Options!.AllowFallback);

        form.TypeIndex = (int)DeploymentObjectType.TaskSequence;
        await DeploymentPageHarness.WaitAsync(() => !form.IsLoading && form.Objects.Count == 1);
        await DeploymentPageHarness.PickAsync(form, _h.Win11, "LAB00010");
        form.TaskSequenceContent = DeploymentText.TaskSequenceContents[1];
        form.TaskSequenceAvailability = DeploymentText.TaskSequenceAvailabilities[0];
        var ts = form.BuildCreateRequest()!.Options!;
        Assert.Equal(("DownloadAllContentLocallyBeforeStartingTaskSequence", "Clients"), (ts.NetworkBehavior, ts.TaskSequenceAvailability));
        form.TaskSequenceAvailability = DeploymentText.TaskSequenceAvailabilities[2];
        Assert.Null(form.BuildCreateRequest());
        Assert.Equal("Download all content before the task sequence starts works only when the task sequence is available to clients only.", form.FormMessage);

        form.TypeIndex = (int)DeploymentObjectType.SoftwareUpdateGroup;
        await DeploymentPageHarness.WaitAsync(() => !form.IsLoading && form.Objects.Count == 1);
        form.SelectedObject = form.Objects.Single();
        form.SelectedCollection = form.Collections.Single(c => c.CollectionId == "LAB00010");
        form.AllowMicrosoftUpdate = true;
        Assert.True(form.BuildCreateRequest()!.Options!.AllowMicrosoftUpdate);
    }

    [Fact]
    public async Task RequiredOnlySettingsAreOffAndLabelledForAnAvailableDeployment()
    {
        var form = await FormAsync();
        Assert.False(form.RequiredOnlyEnabled);
        Assert.Equal(DeploymentFormViewModel.RequiredOnlyText, form.RequiredOnlyNote);
        Assert.Contains("ConfigMgr ignores", DeploymentFormViewModel.RequiredOnlyText);
        SetRequired(form);
        Assert.True(form.RequiredOnlyEnabled);
        Assert.Equal("", form.RequiredOnlyNote);
    }

    [Fact]
    public async Task TheTimeBasisNoteSaysWhatUtcChanges()
    {
        var form = await FormAsync();
        Assert.Equal(DeploymentFormViewModel.LocalNote, form.TimeBasisNote);
        form.UtcBasis = true;
        Assert.Equal(DeploymentFormViewModel.UtcNote, form.TimeBasisNote);
        Assert.Contains("available time", DeploymentFormViewModel.UtcNote);
        form.TypeIndex = (int)DeploymentObjectType.Package;
        Assert.Equal(DeploymentFormViewModel.UtcNote + DeploymentFormViewModel.NoExpirationNote, form.TimeBasisNote);
    }

    [Theory]
    [InlineData(DeploymentObjectType.Application, "ConfigMgr does not allow a second deployment of an application to the same collection.")]
    [InlineData(DeploymentObjectType.Package, "ConfigMgr allows a second deployment of a package, a task sequence or a software update group to the same collection; this page blocks it.")]
    [InlineData(DeploymentObjectType.SoftwareUpdateGroup, "ConfigMgr allows a second deployment of a package, a task sequence or a software update group to the same collection; this page blocks it.")]
    public void AFailedDuplicateCheckSaysWhatConfigMgrAllowsForTheType(DeploymentObjectType type, string note)
    {
        var check = new PreflightCheck(Name: "NoDuplicate", Result: "Fail", Message: "X is already deployed to collection LAB00010 (deployment ID {A}).");
        Assert.Equal($"X is already deployed to collection LAB00010 (deployment ID {{A}}). {note}", DeploymentText.CheckMessage(check, type));
        Assert.Equal("Fine.", DeploymentText.CheckMessage(check with { Result = "Pass", Message = "Fine." }, type));
    }

    [Fact]
    public async Task ASoftwareUpdateGroupShowsItsUpdateCount()
    {
        _h.Service.Objects[DeploymentObjectType.SoftwareUpdateGroup][0] = _h.Updates with { UpdateCount = 12 };
        var page = await _h.OpenAsync();
        page.Deploy.TypeIndex = (int)DeploymentObjectType.SoftwareUpdateGroup;
        await DeploymentPageHarness.WaitAsync(() => !page.Deploy.IsLoading && page.Deploy.Objects.Count == 1);
        Assert.Equal("12 updates", page.Deploy.Objects.Single().Version);
    }

    // ---- History records ------------------------------------------------------------------------

    [Fact]
    public void AHistoryRecordIsCapturedByItsUniqueIdTimeBasisAndProgram()
    {
        var record = new DeploymentAuditRecord(Timestamp: new DateTime(2026, 10, 20, 8, 0, 0), User: @"LAB\admin", DeploymentType: "Package", ApplicationName: "Office Tools",
            ApplicationVersion: "Install", CollectionName: "Pilot Workstations", CollectionID: "LAB00010", MemberCount: 3, DeployPurpose: "Available", DeployAction: "Install",
            DeadlineDateTime: null, DeploymentID: "MCM20020", Result: "Partial")
        {
            Event = "Create", ObjectId = "LAB00020", AvailableDateTime = new DateTime(2026, 10, 21, 9, 0, 0), DeploymentUniqueID = "MCM20020", ProgramName = "Install", TimeBasedOn = "Utc"
        };
        var captured = DeploymentHistoryViewModel.Captured(record)!;
        Assert.Equal(("MCM20020", true, "Install"), (captured.DeploymentId, captured.UtcBasis, captured.Program));
        Assert.True(new DeploymentHistoryRow(record).CanUndo);

        var application = record with { DeploymentType = "Application", DeploymentID = "16777500", DeploymentUniqueID = "{AB-1}", Result = "Success", TimeBasedOn = "LocalTime" };
        Assert.Equal("{AB-1}", DeploymentHistoryViewModel.Captured(application)!.DeploymentId);
        Assert.False(DeploymentHistoryViewModel.Captured(application)!.UtcBasis);

        Assert.False(new DeploymentHistoryRow(record with { Result = "Failed" }).CanUndo);
        Assert.False(new DeploymentHistoryRow(record with { Result = "Unknown" }).CanUndo);
        Assert.Equal("Unknown: check ConfigMgr", new DeploymentHistoryRow(record with { Result = "Unknown" }).Result);
        Assert.Equal("Partial: made with a problem", new DeploymentHistoryRow(record).Result);
        Assert.Equal("Success", new DeploymentHistoryRow(application).Result);
        Assert.Equal("Failed: text of the suite", new DeploymentHistoryRow(record with { Result = "Failed: text of the suite" }).Result);
        Assert.Equal("UTC", new DeploymentHistoryRow(record).Basis);
        Assert.Equal("Local time", new DeploymentHistoryRow(application).Basis);
    }

    // ---- Named arguments ------------------------------------------------------------------------

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "ApplicationPackagerUI"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src");
    }

    private static readonly string[] ContractRecords =
        ["DeploymentChange", "DeploymentOutcome", "DeploymentOptions", "DeployableObject", "DeploymentRecord", "PreflightCheck", "DeploymentAuditRecord", "DeploymentTemplate", "DpGroupInfo"];

    /// <summary>Every argument of each construction of a contract record in the page code and its tests carries its parameter name.</summary>
    [Fact]
    public void EveryConstructionOfAContractRecordUsesNamedArguments()
    {
        var root = SourceRoot();
        var tests = Path.Combine(Path.GetDirectoryName(root)!, "tests", "ApplicationPackagerEngine.Tests", "Deployments");
        var files = Directory.EnumerateFiles(Path.Combine(root, "ApplicationPackagerUI"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Concat(new[] { "DeploymentPageHarness.cs", "DeploymentWriterTests.cs", "DeploymentHelperRenderTests.cs", "DeploymentHelperViewModelTests.cs",
                "DeploymentHistoryOptionsTests.cs", "DeploymentPageServiceTests.cs", "DeploymentPageContractTests.cs" }.Select(f => Path.Combine(tests, f)));
        var pattern = new Regex(@"\bnew\s+(" + string.Join("|", ContractRecords) + @")\s*\(", RegexOptions.CultureInvariant);
        var positional = new List<string>();
        foreach (var file in files)
        {
            var code = MaintenanceWindowWriterTests.Code(File.ReadAllText(file));
            foreach (Match match in pattern.Matches(code))
            {
                var arguments = Arguments(code, match.Index + match.Length);
                if (arguments.Count == 0) continue;
                if (arguments.Any(a => !Regex.IsMatch(a, @"^\s*[A-Za-z_][A-Za-z0-9_]*\s*:(?!:)")))
                    positional.Add($"{Path.GetFileName(file)}: new {match.Groups[1].Value}(...)");
            }
        }
        Assert.Empty(positional);
    }

    /// <summary>The top-level arguments of the call whose argument list starts at <paramref name="start"/>.</summary>
    private static List<string> Arguments(string code, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();
        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '(' or '[' or '{') depth++;
            if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (current.ToString().Trim().Length > 0) arguments.Add(current.ToString());
                    return arguments;
                }
                depth--;
            }
            if (c == ',' && depth == 0)
            {
                arguments.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        return arguments;
    }

    private DeploymentChange Change(DeploymentChangeKind kind, string purpose) => new(
        Kind: kind, Object: _h.SevenZip, Collection: new DeploymentCollection("LAB00010", "Pilot Workstations", "", 3),
        Options: new DeploymentOptions(Purpose: purpose, AvailableTime: new DateTime(2026, 10, 20, 10, 0, 0), Deadline: purpose == "Required" ? new DateTime(2026, 10, 22, 8, 0, 0) : null,
            UtcBasis: false, UserNotification: "DisplayAll", OverrideServiceWindow: false, RebootOutsideServiceWindow: false, AllowMetered: false,
            NetworkBehavior: "", RerunBehavior: "", ShowProgress: false, AllowFallback: false, FallbackToDp: false, PostRebootScan: false, SuppressRestart: false),
        DpGroups: [], CreateNowContentDistributing: false, RequiredConfirmed: false, BackdatedAcknowledged: false, Existing: null,
        Checks: [], Impact: [], CreatedId: null);
}
