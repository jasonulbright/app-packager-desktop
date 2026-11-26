using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.AddFlow;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerEngine.Tests.Wsus;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The "No unsaved changes" line of Settings (overhaul V1, phase 7b, Q-126): the page is dirty after an edit and clean after Save
/// Settings and after Discard changes. The tests drive the full Settings view model over a data folder of their own; the GitHub query
/// never answers, and no window opens.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsUnsavedChangesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-settings-dirty-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SettingsUnsavedChangesTests()
    {
        foreach (var folder in new[] { "", "packagers", "packagers2", "staging" }) Directory.CreateDirectory(Path.Combine(_root, folder));
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        TestCleanup.DeleteDirectory(_root);
    }

    private (SettingsViewModel ViewModel, SettingsService Service) Create(Action<AppSettings>? stored = null)
    {
        var service = new SettingsService();
        // No site code: a save then starts no per-site layout.
        var settings = new AppSettings
        {
            CompanyName = "Contoso", SiteCode = "", SiteCodeSaved = true, Theme = "Dark", StagingRootMigrated = true,
            PackagersRoot = Path.Combine(_root, "packagers"), DownloadRoot = Path.Combine(_root, "staging"),
            Systems = new SystemsInUse { ConfigMgr = true }
        };
        stored?.Invoke(settings);
        service.Save(settings);

        var packages = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
        packages.Packagers =
        [
            new PackagerInfo { Uid = "app1", Vendor = "Contoso", Application = "Contoso One" },
            new PackagerInfo { Uid = "app2", Vendor = "Contoso", Application = "Contoso Two" }
        ];
        var discovery = new PackagerDiscoveryService(service);
        var viewModel = new SettingsViewModel(service, new PackagerPreferencesService(service, discovery), new FakeSnackbar(), packages, discovery,
            new FakeWsusAdminClient(), new FakeWsusPrompts(), new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(null)),
            gitHubQuery: () => new TaskCompletionSource<GitHubApiStatus>().Task)
        { RefreshPublishState = () => { } };
        // The install parameters and the path rows load from the definitions after the constructor.
        StaTestHelper.PumpUntil(() => viewModel.InstallParameterSummary.Length > 0);
        return (viewModel, service);
    }

    private static void OnSta(Action test) => StaTestHelper.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        test();
    });

    private static void AssertClean(SettingsViewModel viewModel, string after)
    {
        Assert.True(!viewModel.HasUnsavedChanges, $"Dirty after {after}: {string.Join(", ", viewModel.ChangedEntries())}");
        Assert.Equal("No unsaved changes", viewModel.UnsavedChangesText);
    }

    private static void AssertDirty(SettingsViewModel viewModel, string after)
    {
        Assert.True(viewModel.HasUnsavedChanges, $"Clean after {after}.");
        Assert.Equal("Unsaved changes", viewModel.UnsavedChangesText);
    }

    /// <summary>One edit of each kind of field. The name is the binding path where the page has one. Each edit differs from a first load.</summary>
    private Dictionary<string, Action<SettingsViewModel>> Edits() => new()
    {
        ["CompanyName"] = v => v.CompanyName = "Fabrikam",
        ["CheckForUpdatesAtStartup"] = v => v.CheckForUpdatesAtStartup = !v.CheckForUpdatesAtStartup,
        ["CurrentTheme"] = v => v.CurrentTheme = Wpf.Ui.Appearance.ApplicationTheme.Light,
        ["SiteCode"] = v => v.SiteCode = "AB1",
        ["ProviderMachine"] = v => v.ProviderMachine = "cm01.contoso.com",
        ["FileShareRoot"] = v => v.FileShareRoot = @"\\files.contoso.com\content",
        ["DownloadRoot"] = v => v.DownloadRoot = Path.Combine(_root, "staging2"),
        ["PackagersRoot"] = v => v.PackagersRoot = Path.Combine(_root, "packagers2"),
        ["Discovery.SqlServer"] = v => v.Discovery.SqlServer = "sql.contoso.com",
        ["Discovery.SqlDatabase"] = v => v.Discovery.SqlDatabase = "CM_AB1",
        ["Discovery.EncryptConnection"] = v => v.Discovery.EncryptConnection = true,
        ["Discovery.TrustServerCertificate"] = v => v.Discovery.TrustServerCertificate = true,
        ["EstimatedRuntimeMins"] = v => v.EstimatedRuntimeMins = 21,
        ["MaximumRuntimeMins"] = v => v.MaximumRuntimeMins = 44,
        ["ContentFallback"] = v => v.ContentFallback = v.ContentFallback == "Deny" ? "Allow" : "Deny",
        ["SlowNetworkDeploymentMode"] = v => v.SlowNetworkDeploymentMode = v.SlowNetworkDeploymentMode == "DoNothing" ? "Download" : "DoNothing",
        ["TitleOptions.IncludeVersionInTitle"] = v => v.TitleOptions.IncludeVersionInTitle = !v.TitleOptions.IncludeVersionInTitle,
        ["TitleOptions.AppendArchitectureToTitle"] = v => v.TitleOptions.AppendArchitectureToTitle = !v.TitleOptions.AppendArchitectureToTitle,
        ["Signing.SignDetection"] = v => v.Signing.SignDetection = !v.Signing.SignDetection,
        ["Signing.TimestampServer"] = v => v.Signing.TimestampServer = "http://timestamp.contoso.com",
        ["M365Channel"] = v => v.M365Channel = "Current",
        ["ContentLayout"] = v => v.ContentLayout = "Flat",
        ["AutoDistribute"] = v => v.AutoDistribute = !v.AutoDistribute,
        ["DpGroupName"] = v => v.DpGroupName = "All DPs of Contoso",
        ["DeployToTestCollection"] = v => v.DeployToTestCollection = !v.DeployToTestCollection,
        ["TestCollectionName"] = v => v.TestCollectionName = "Pilot",
        ["TestCollectionIsUser"] = v => v.TestCollectionIsUser = !v.TestCollectionIsUser,
        ["CreateTestCollectionIfMissing"] = v => v.CreateTestCollectionIfMissing = !v.CreateTestCollectionIfMissing,
        ["UseIntune"] = v => v.UseIntune = !v.UseIntune,
        ["Wsus.ServerName"] = v => v.Wsus.ServerName = "wsus01.contoso.com",
        ["Wsus.Port"] = v => v.Wsus.Port = "9000",
        ["Wsus.UseSsl"] = v => v.Wsus.UseSsl = !v.Wsus.UseSsl,
        ["Wsus.ApprovalGroup"] = v => v.Wsus.ApprovalGroup = "Pilot computers",
        ["Wsus.DeclineSuperseded"] = v => v.Wsus.DeclineSuperseded = !v.Wsus.DeclineSuperseded,
        ["IntuneTenantId"] = v => v.IntuneTenantId = "contoso.onmicrosoft.com",
        ["IntuneClientId"] = v => v.IntuneClientId = "11111111-1111-1111-1111-111111111111",
        ["SkipConfigReview"] = v => v.SkipConfigReview = !v.SkipConfigReview,
        // The Citrix switches and the install parameters raise no change notice: the page asks after an input event.
        ["CwaSwitches"] = v => { v.CwaSwitches.Store!.Name = "Store of Contoso"; v.RefreshDirtyState(); },
        ["InstallParameterGroups"] = v =>
        {
            var group = v.InstallParameterGroups.First();
            group.Values[group.Parameters[0].Key] = "changed by the test";
            v.RefreshDirtyState();
        },
        ["LocalSourceRows"] = v => v.LocalSourceRows.First().Path = Path.Combine(_root, "installers"),
        ["OneClickAction"] = v => v.OneClickAction = "Stage",
        ["OneClickForce"] = v => v.OneClickForce = !v.OneClickForce,
        ["OneClickCadenceDays"] = v => v.OneClickCadenceDays = 12,
        ["OneClickDefaultWsus"] = v => v.OneClickDefaultWsus = !v.OneClickDefaultWsus,
        ["OneClickOnExisting"] = v => v.OneClickOnExisting = "Overwrite",
        ["Tracked"] = v => v.OneClickRows[0].Tracked = !v.OneClickRows[0].Tracked,
        ["CadenceDisplay"] = v => v.OneClickRows[0].CadenceDisplay = "30",
        ["ConfigMgrVersionRule"] = v => v.ConfigMgrVersionRule = "Exact",
        ["IntuneVersionRule"] = v => v.IntuneVersionRule = "Exact",
        ["SupersedenceMode"] = v => v.SupersedenceMode = "Replace",
        ["IntuneClientSecret"] = v => v.IntuneClientSecret = "test-value",
        ["NvdApiKey"] = v => v.NvdApiKey = "test-value",
        ["GitHubToken"] = v => v.GitHubToken = "test-value",
        ["LookupCvesAfterCheckMecm"] = v => v.LookupCvesAfterCheckMecm = !v.LookupCvesAfterCheckMecm
    };

    [Fact]
    public void APageThatWasJustLoadedHasNoUnsavedChanges() => OnSta(() => AssertClean(Create().ViewModel, "the load"));

    [Fact]
    public void EveryEditMakesThePageDirtyAndDiscardMakesItClean() => OnSta(() =>
    {
        var (viewModel, _) = Create();
        foreach (var (name, edit) in Edits())
        {
            edit(viewModel);
            AssertDirty(viewModel, "the edit of " + name);
            viewModel.DiscardChangesCommand.Execute(null);
            AssertClean(viewModel, "Discard changes after " + name);
        }
    });

    [Fact]
    public void EveryEditMakesThePageDirtyAndSaveMakesItClean() => OnSta(() =>
    {
        var (viewModel, _) = Create();
        // A new site code switches the site; its save is another flow.
        foreach (var (name, edit) in Edits().Where(e => e.Key != "SiteCode"))
        {
            edit(viewModel);
            AssertDirty(viewModel, "the edit of " + name);
            viewModel.SaveSettingsCommand.Execute(null);
            AssertClean(viewModel, "Save Settings after " + name);
        }
    });

    [Fact]
    public void EveryValueBoxOfThePageHasAnEditInTheseTests()
    {
        var page = XDocument.Load(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Pages", "SettingsPage.xaml"));
        var bound = page.Descendants()
            .SelectMany(e => e.Attributes().Where(a => a.Name.LocalName is "IsChecked" or "Text" or "SelectedItem" or "SelectedValue" or "Value" or "Password")
                .Where(a => e.Name.LocalName is "TextBox" or "PasswordBox" or "ComboBox" or "CheckBox" or "NumberBox" or "RadioButton"))
            .Select(a => System.Text.RegularExpressions.Regex.Match(a.Value, @"^\{Binding ([A-Za-z0-9_.]+)").Groups[1].Value)
            .Where(path => path.Length > 0)
            .Distinct()
            .ToList();
        // Not part of the edit buffer: the bulk bar and the search box of the grid, and the rows that apply at once (Sidebar).
        string[] notSettings = ["BulkColumn", "BulkValue", "BulkScope", "ApplicationFilter", "IsSelected", "Shown", "Title"];
        // Covered under the name of their section, or stored only with a saved value (the two Remove boxes, tested apart).
        var edits = Edits().Keys.ToHashSet();
        bool Covered(string path) => edits.Contains(path)
            || (path.StartsWith("CwaSwitches.") && edits.Contains("CwaSwitches"))
            || (path == "Path" && edits.Contains("LocalSourceRows"))
            || path is "RemoveNvdApiKey" or "RemoveGitHubToken"
            // Fields of a section that one edit of the section covers.
            || path.StartsWith("Signing.") || path.StartsWith("TitleOptions.") || path.StartsWith("Wsus.")
            || path is "UseConfigMgr" or "UseWsus" or "OneClickDefaultConfigMgr" or "OneClickDefaultIntune" or "IntuneMinimumWindowsRelease"
                or "ConfigMgr" or "Intune" or "Wsus";

        var missing = bound.Where(path => !notSettings.Contains(path) && !Covered(path)).ToList();

        Assert.True(missing.Count == 0, "The dirty line has no test for these boxes: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheBoxesWithoutAnEditOfTheirOwnMakeThePageDirtyToo() => OnSta(() =>
    {
        var (viewModel, _) = Create();
        var edits = new Dictionary<string, Action>
        {
            ["UseConfigMgr"] = () => viewModel.UseConfigMgr = !viewModel.UseConfigMgr,
            ["UseWsus"] = () => viewModel.UseWsus = !viewModel.UseWsus,
            ["OneClickDefaultConfigMgr"] = () => viewModel.OneClickDefaultConfigMgr = !viewModel.OneClickDefaultConfigMgr,
            ["OneClickDefaultIntune"] = () => viewModel.OneClickDefaultIntune = !viewModel.OneClickDefaultIntune,
            ["IntuneMinimumWindowsRelease"] = () => viewModel.IntuneMinimumWindowsRelease = ApplicationPackagerEngine.Services.IntuneWindowsReleases.Known.First(r => r != viewModel.IntuneMinimumWindowsRelease),
            ["Wsus.Classification"] = () => viewModel.Wsus.Classification = "SecurityUpdates",
            ["Signing.RequireDeployment"] = () => viewModel.Signing.RequireDeployment = !viewModel.Signing.RequireDeployment,
            ["Signing.TimestampRequired"] = () => viewModel.Signing.TimestampRequired = !viewModel.Signing.TimestampRequired
        };
        foreach (var (name, edit) in edits)
        {
            edit();
            AssertDirty(viewModel, "the edit of " + name);
            viewModel.DiscardChangesCommand.Execute(null);
            AssertClean(viewModel, "Discard changes after " + name);
        }
    });

    [Fact]
    public void AnEditThatIsChangedBackLeavesThePageClean() => OnSta(() =>
    {
        var (viewModel, _) = Create();

        viewModel.CompanyName = "Fabrikam";
        viewModel.CompanyName = "Contoso";
        AssertClean(viewModel, "a change back");

        // Save Settings trims this box, so spaces around the stored value are no edit.
        viewModel.ProviderMachine = "   ";
        AssertClean(viewModel, "spaces in a box that Save Settings trims");
    });

    [Fact]
    public void TheCheckBoxThatRemovesASavedKeyIsAnUnsavedChange() => OnSta(() =>
    {
        var (viewModel, _) = Create(s => s.GitHubTokenProtected = GitHubTokenStore.Protect("test-value"));

        viewModel.RemoveGitHubToken = true;
        AssertDirty(viewModel, "Remove the saved token");
        viewModel.SaveSettingsCommand.Execute(null);
        AssertClean(viewModel, "Save Settings");
    });

    [Fact]
    public void AGridEditIsAnUnsavedChangeUntilItIsSavedDiscardedOrUndone() => OnSta(() =>
    {
        var (viewModel, _) = Create();
        SupersedenceRow Row() => new() { Uid = "app1", ApplicationId = "catalog:app1", DefinitionId = "app1", Application = "Contoso One", Vendor = "Contoso" };
        void Bulk(string column, string value)
        {
            viewModel.SupersedenceRows.Clear();
            viewModel.SupersedenceRows.Add(Row());
            viewModel.BulkColumn = column;
            viewModel.BulkValue = value;
            viewModel.BulkScope = "All applications";
            viewModel.ApplyBulkNowCommand.Execute(null);
            // The list is rebuilt from the App Packager page after a load; this test has no grid facts to rebuild it from.
            viewModel.SupersedenceRows.Clear();
        }

        foreach (var (column, value, back) in new[]
                 {
                     ("Supersedence", "Replace", "Follow the default"),
                     ("Version rule", "Exact", "Follow the default"),
                     ("Install command", "setup.exe /s", ""),
                     ("Distribution point group", "Pilot DPs", ""),
                     ("Estimated runtime (min)", "30", "")
                 })
        {
            Bulk(column, value);
            AssertDirty(viewModel, "a bulk edit of " + column);
            Bulk(column, back);
            AssertClean(viewModel, "the bulk edit of " + column + " back");
            Bulk(column, value);
            viewModel.DiscardChangesCommand.Execute(null);
            AssertClean(viewModel, "Discard changes after " + column);
        }

        Bulk("Supersedence", "Replace");
        viewModel.SaveSettingsCommand.Execute(null);
        AssertClean(viewModel, "Save Settings after a grid edit");
    });

    [Fact]
    public void AValueThatSaveCannotStoreAsTypedKeepsThePageDirty() => OnSta(() =>
    {
        var (viewModel, service) = Create();

        viewModel.Wsus.Port = "not a port";
        viewModel.SaveSettingsCommand.Execute(null);

        AssertDirty(viewModel, "a save of a port that is not a number");
        Assert.Equal(WsusSettings.HttpPort, service.Load().Wsus.PortNumber);
    });

    [Fact]
    public void ASaveThatFailsKeepsTheEditsUnsaved() => OnSta(() =>
    {
        var (viewModel, service) = Create(s => s.DiscoveryDismissed = ["one"]);
        viewModel.Discovery.ClearDismissedCommand.Execute(null);
        AssertDirty(viewModel, "Clear list");
        // A service with run overrides refuses to write.
        service.ApplyRunOverrides(_ => { });

        viewModel.SaveSettingsCommand.Execute(null);

        AssertDirty(viewModel, "a save that failed");
        Assert.True(viewModel.Discovery.ClearPending);
    });

    /// <summary>
    /// Reads the source: every property of the view model and every working copy of the grid that Save Settings puts into the file is
    /// part of the buffer that the dirty line compares.
    /// </summary>
    [Fact]
    public void EveryValueThatSaveSettingsWritesIsPartOfTheComparedBuffer()
    {
        var folder = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "ViewModels", "Pages");
        static string Between(string text, string from, string to)
        {
            var start = text.IndexOf(from, StringComparison.Ordinal);
            var end = start < 0 ? -1 : text.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end > start, $"The source no longer has the text from '{from}' to '{to}'.");
            return text[start..end];
        }

        // The part of the save that builds what is written, and the capture of the buffer.
        var save = Between(File.ReadAllText(Path.Combine(folder, "SettingsViewModel.cs")), "private void SaveSettings()", "_settingsService.SaveOperatorEdit(s);");
        var capture = Between(File.ReadAllText(Path.Combine(folder, "SettingsViewModel.Unsaved.cs")),
            "private Dictionary<string, string> CaptureEditBuffer()", "private static string Lines(");
        static bool Names(string source, string name) => System.Text.RegularExpressions.Regex.IsMatch(source, $@"(?<![\w.]){name}\b");

        var properties = typeof(SettingsViewModel).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .Where(name => Names(save, name))
            // The three boxes of the One Click defaults, which the save reads through one property.
            .SelectMany(name => name == nameof(SettingsViewModel.OneClickDefaultDestinations)
                ? new[] { "OneClickDefaultConfigMgr", "OneClickDefaultWsus", "OneClickDefaultIntune" }
                : [name])
            .Distinct()
            .ToList();
        Assert.True(properties.Count > 40, "The save names too few properties: " + string.Join(", ", properties));
        var missing = properties.Where(name => !Names(capture, name)).ToList();

        // The working copies of the grid; the Touched sets only say which of them the save writes.
        var copies = System.Text.RegularExpressions.Regex.Matches(save, @"\b_work\.(\w+)").Select(m => m.Groups[1].Value)
            .Where(name => !name.EndsWith("Touched", StringComparison.Ordinal))
            .Select(name => name == "ProfilePending" ? "_work.ProfileEdits" : "_work." + name)
            .Distinct();
        missing.AddRange(copies.Where(name => !capture.Contains(name, StringComparison.Ordinal)));
        if (save.Contains("_supersedenceWork", StringComparison.Ordinal) && !capture.Contains("_supersedenceWork", StringComparison.Ordinal))
            missing.Add("_supersedenceWork");

        Assert.True(missing.Count == 0, "Save Settings writes these, and the dirty line does not compare them: " + string.Join(", ", missing));
    }

    [Fact]
    public void ADistributionOverrideSurvivesASaveThatFailedAndIsWrittenByTheNextOne() => OnSta(() =>
    {
        var (viewModel, service) = Create();
        viewModel.SupersedenceRows.Add(new SupersedenceRow { Uid = "app1", ApplicationId = "catalog:app1", DefinitionId = "app1", Application = "Contoso One", Vendor = "Contoso" });
        viewModel.BulkColumn = "Distribution point group";
        viewModel.BulkValue = "Pilot DPs";
        viewModel.BulkScope = "All applications";
        viewModel.ApplyBulkNowCommand.Execute(null);
        viewModel.SupersedenceRows.Clear();

        // A reader that does not share delete access: the settings can be read and cannot be replaced.
        using (new FileStream(SettingsService.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            viewModel.SaveSettingsCommand.Execute(null);

        AssertDirty(viewModel, "a save that failed");
        Assert.Empty(service.Load().DistributionOverrides);

        viewModel.SaveSettingsCommand.Execute(null);

        AssertClean(viewModel, "the second save");
        Assert.Equal("Pilot DPs", service.Load().DistributionOverrides["catalog:app1"].DpGroupName);
    });

    [Fact]
    public void TypedTextThatABoxDidNotWriteYetCountsAsUnsaved() => OnSta(() =>
    {
        var (viewModel, _) = Create();

        viewModel.HasPendingEntry = true;
        AssertDirty(viewModel, "typing in a number box");
        viewModel.HasPendingEntry = false;
        AssertClean(viewModel, "the end of the entry");
    });

    [Fact]
    public void NewSystemsInUseShowTheOneClickDefaultsThatTheSaveStored() => OnSta(() =>
    {
        var (viewModel, service) = Create();

        viewModel.UseIntune = true;
        viewModel.SaveSettingsCommand.Execute(null);

        var stored = service.Load().OneClick.DefaultDestinations!;
        Assert.Equal((stored.ConfigMgr, stored.Wsus, stored.Intune),
            (viewModel.OneClickDefaultConfigMgr, viewModel.OneClickDefaultWsus, viewModel.OneClickDefaultIntune));
        AssertClean(viewModel, "a save of new Systems in Use");
    });

    [Fact]
    public void TheColumnChoiceIsSavedAtOnceAndIsNotAnUnsavedChange() => OnSta(() =>
    {
        var (viewModel, service) = Create();

        viewModel.SetColumnVisible("dpgroup", true);
        viewModel.SetColumnVisible("scope", false);

        AssertClean(viewModel, "a change of the columns");
        var saved = service.Load().ApplicationSettingsColumns!;
        Assert.Contains("dpgroup", saved);
        Assert.DoesNotContain("scope", saved);
        Assert.Equal(ApplicationColumns.VisibleIds(saved).Order(), viewModel.VisibleColumnIds.Order());
    });

    [Fact]
    public void WithoutASavedChoiceTheGridShowsTheDefaultColumnsAndAnUnknownIdIsDropped()
    {
        Assert.Equal(ApplicationColumns.All.Where(c => c.DefaultVisible).Select(c => c.Id).Order(), ApplicationColumns.VisibleIds(null).Order());
        Assert.Equal(["rule"], ApplicationColumns.VisibleIds(["rule", "no such column"]));
    }
}
