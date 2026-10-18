using System.Diagnostics;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

internal sealed class FakeSuiteFileSystem : ISuiteFileSystem
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Reads { get; } = [];

    public FakeSuiteFileSystem Add(string path, string? text = null)
    {
        _files.Add(Path.GetFullPath(path));
        if (text is not null) _texts[Path.GetFullPath(path)] = text;
        return this;
    }

    public bool FileExists(string path) => _files.Contains(Path.GetFullPath(path));

    public string? ReadText(string path)
    {
        Reads.Add(path);
        return _texts.TryGetValue(Path.GetFullPath(path), out var text) ? text : null;
    }
}

internal sealed class FakeSuiteEnvironment : ISuiteEnvironment
{
    public string LocalApplicationData { get; set; } = @"C:\Users\op\AppData\Local";
    public string WindowsPowerShellPath { get; set; } = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    public string? Registered { get; set; }
    public string? RegisteredInstallLocation() => Registered;
}

internal sealed class FakeToolProcess : IToolProcess
{
    public required int Id { get; init; }
    public bool HasExited { get; set; }
    public int FrontRequests { get; private set; }
    public bool BringToFront()
    {
        FrontRequests++;
        return true;
    }
}

internal sealed class FakeToolProcessStarter : IToolProcessStarter
{
    private int _nextId = 1000;
    public List<ProcessStartInfo> Started { get; } = [];
    public List<FakeToolProcess> Processes { get; } = [];
    public Exception? Failure { get; set; }

    public IToolProcess Start(ProcessStartInfo startInfo)
    {
        if (Failure is not null) throw Failure;
        Started.Add(startInfo);
        var process = new FakeToolProcess { Id = _nextId++ };
        Processes.Add(process);
        return process;
    }
}

public sealed class SuiteToolLauncherTests
{
    private const string Local = @"C:\Users\op\AppData\Local";
    private const string Install = Local + @"\AppPackagerSuite";
    private const string Launcher = Install + @"\app-packager-suite\start-suite.ps1";
    private const string Settings = Install + @"\app-packager-suite\suite.settings.json";
    private static readonly SuiteTool Detection = SuiteTools.All.Single(t => t.Id == "detection-tester");

    private readonly FakeSuiteFileSystem _files = new();
    private readonly FakeSuiteEnvironment _environment = new();
    private readonly FakeToolProcessStarter _starter = new();

    private SuiteToolLauncher Launcher_() => new(_files, _environment, _starter);

    private void InstallSuite(string root = Install, string? settings = null)
    {
        _files.Add(Path.Combine(root, "app-packager-suite", "start-suite.ps1"));
        if (settings is not null) _files.Add(Path.Combine(root, "app-packager-suite", "suite.settings.json"), settings);
        _files.Add(_environment.WindowsPowerShellPath);
    }

    private void InstallTool(SuiteTool tool, string root = Install) => _files.Add(Path.Combine(root, tool.Folder, tool.EntryScript));

    [Fact]
    public void TheEightToolsNameTheSuiteFoldersAndEntryScripts()
    {
        Assert.Equal(
        [
            ("detection-tester", "detection-tester", "start-detectiontester.ps1"),
            ("maintenance-windows", "maintenance-window-manager", "start-maintenancewindowmgr.ps1"),
            ("collection-and-compliance", "collection-and-compliance-manager", "start-ccm.ps1"),
            ("installer-analysis", "installer-analysis", "start-installeranalysis.ps1"),
            ("deployment-helper", "deployment-helper", "start-deploymenthelper.ps1"),
            ("dp-content-manager", "dp-content-manager", "start-dpcontentmgr.ps1"),
            ("pivots-and-scripts", "pivots-and-scripts", "start-pivotsandscripts.ps1"),
            ("site-hygiene", "site-hygiene", "start-sitehygiene.ps1")
        ], SuiteTools.All.Select(t => (t.Id, t.Folder, t.EntryScript)));
        Assert.DoesNotContain(SuiteTools.All, t => t.Folder == "collection-manager");
    }

    [Fact]
    public void AnInstalledToolResolvesUnderTheDefaultInstallFolder()
    {
        InstallSuite();
        InstallTool(Detection);
        var resolution = Launcher_().Resolve(Detection);
        Assert.Null(resolution.Message);
        Assert.Equal(Install + @"\detection-tester", resolution.Location!.ToolFolder);
        Assert.Equal(Install + @"\detection-tester\start-detectiontester.ps1", resolution.Location.EntryScript);
    }

    [Fact]
    public void TheRegisteredInstallLocationComesBeforeTheDefaultFolder()
    {
        _environment.Registered = @"D:\Tools\Suite";
        InstallSuite(@"D:\Tools\Suite");
        InstallSuite();
        InstallTool(Detection, @"D:\Tools\Suite");
        InstallTool(Detection);
        Assert.Equal([@"D:\Tools\Suite", Install], Launcher_().SuiteRoots());
        Assert.Equal(@"D:\Tools\Suite\detection-tester", Launcher_().Resolve(Detection).Location!.ToolFolder);
    }

    [Fact]
    public void ARegisteredLocationWithoutTheSuiteFallsBackToTheDefaultFolder()
    {
        _environment.Registered = @"D:\Gone";
        InstallSuite();
        InstallTool(Detection);
        Assert.Equal(Install + @"\detection-tester", Launcher_().Resolve(Detection).Location!.ToolFolder);
    }

    [Fact]
    public void AMissingSuiteNamesTheSuiteAndEveryFolderSearched()
    {
        _environment.Registered = @"D:\Gone";
        var resolution = Launcher_().Resolve(Detection);
        Assert.Null(resolution.Location);
        Assert.Equal(
            @"Detection Tester needs AppPackager Suite, which is not installed. Looked for app-packager-suite\start-suite.ps1 in: D:\Gone; " + Install + ".",
            resolution.Message);
    }

    [Fact]
    public void AMissingToolNamesTheToolAndWhereItLooked()
    {
        InstallSuite();
        var resolution = Launcher_().Resolve(Detection);
        Assert.Null(resolution.Location);
        Assert.Equal(
            @"Detection Tester is not in the AppPackager Suite install. Looked for start-detectiontester.ps1 in: " + Install + @"\detection-tester.",
            resolution.Message);
    }

    [Fact]
    public void ToolsRootFromTheSuiteSettingsIsSearchedBeforeTheInstallFolder()
    {
        InstallSuite(settings: """{ "DarkMode": true, "ToolsRoot": "E:\\dev", "SiteCode": "" }""");
        InstallTool(Detection, @"E:\dev");
        InstallTool(Detection);
        Assert.Equal(@"E:\dev\detection-tester", Launcher_().Resolve(Detection).Location!.ToolFolder);
    }

    [Fact]
    public void AToolMissingUnderToolsRootIsFoundInTheInstallFolderAndTheMessageListsBoth()
    {
        InstallSuite(settings: "\uFEFF{ \"ToolsRoot\": \"E:\\\\dev\" }");
        InstallTool(Detection);
        Assert.Equal(Install + @"\detection-tester", Launcher_().Resolve(Detection).Location!.ToolFolder);

        var site = SuiteTools.All.Single(t => t.Id == "site-hygiene");
        Assert.Equal(
            @"Site Hygiene is not in the AppPackager Suite install. Looked for start-sitehygiene.ps1 in: E:\dev\site-hygiene; " + Install + @"\site-hygiene.",
            Launcher_().Resolve(site).Message);
    }

    [Fact]
    public void ARelativeToolsRootIsTakenFromTheSuiteLauncherFolder()
    {
        InstallSuite(settings: """{ "ToolsRoot": "..\\side" }""");
        InstallTool(Detection, Install + @"\side");
        Assert.Equal(Install + @"\side\detection-tester", Launcher_().Resolve(Detection).Location!.ToolFolder);
    }

    [Theory]
    [InlineData("""{ "ToolsRoot": "" }""")]
    [InlineData("""{ "ToolsRoot": null }""")]
    [InlineData("""{ "ToolsRoot": 5 }""")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "ToolsRoot": "E:\\bad|name" }""")]
    public void AnEmptyOrUnreadableToolsRootLeavesTheInstallFolder(string settings)
    {
        InstallSuite(settings: settings);
        InstallTool(Detection);
        Assert.Equal(Install + @"\detection-tester", Launcher_().Resolve(Detection).Location!.ToolFolder);
    }

    [Fact]
    public void TheStartInfoRunsWindowsPowerShellOnTheEntryScriptInTheToolFolder()
    {
        var location = new SuiteToolLocation(@"C:\S\detection-tester", @"C:\S\detection-tester\start-detectiontester.ps1");
        var info = Launcher_().BuildStartInfo(location, "MCM", "cm01.contoso.com");
        Assert.Equal(_environment.WindowsPowerShellPath, info.FileName);
        Assert.Equal(["-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", location.EntryScript], info.ArgumentList);
        Assert.Equal("", info.Arguments);
        Assert.Equal(location.ToolFolder, info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal("MCM", info.Environment[SettingsService.LauncherSiteCodeVariable]);
        Assert.Equal("cm01.contoso.com", info.Environment[SettingsService.LauncherProviderVariable]);
    }

    [Fact]
    public void SiteValuesTravelOnlyAsTrimmedEnvironmentVariables()
    {
        var location = new SuiteToolLocation(@"C:\S\t", @"C:\S\t\start.ps1");
        const string site = "M'C\"M; $(x)";
        const string provider = "cm01'; Remove-Item C:\\ -Recurse #";
        var info = Launcher_().BuildStartInfo(location, "  " + site + " ", provider + "\t");
        Assert.Equal(site, info.Environment[SettingsService.LauncherSiteCodeVariable]);
        Assert.Equal(provider, info.Environment[SettingsService.LauncherProviderVariable]);
        Assert.DoesNotContain(info.ArgumentList, a => a.Contains("M'C", StringComparison.Ordinal) || a.Contains("cm01", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEnvironmentVariableNamesAreTheOnesTheSuiteReads()
    {
        Assert.Equal("SUITE_CM_SITECODE", SettingsService.LauncherSiteCodeVariable);
        Assert.Equal("SUITE_CM_PROVIDER", SettingsService.LauncherProviderVariable);
    }

    [Fact]
    public void LaunchStartsTheToolWithTheSiteValuesAndReportsStarted()
    {
        InstallSuite();
        InstallTool(Detection);
        var result = Launcher_().Launch(Detection, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Started, result.Outcome);
        Assert.Null(result.Message);
        var info = Assert.Single(_starter.Started);
        Assert.Equal(Install + @"\detection-tester\start-detectiontester.ps1", info.ArgumentList[^1]);
        Assert.Equal("MCM", info.Environment[SettingsService.LauncherSiteCodeVariable]);
    }

    [Fact]
    public void ASecondLaunchOfARunningToolBringsItToTheFrontInsteadOfStartingAnother()
    {
        InstallSuite();
        InstallTool(Detection);
        var launcher = Launcher_();
        launcher.Launch(Detection, "MCM", "cm01");
        var result = launcher.Launch(Detection, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.BroughtToFront, result.Outcome);
        Assert.Single(_starter.Started);
        Assert.Equal(1, _starter.Processes[0].FrontRequests);
        Assert.Equal(_starter.Processes[0].Id, launcher.RunningProcessId(Detection));
    }

    [Fact]
    public void AToolWhoseProcessExitedIsForgottenAndStartsAgain()
    {
        InstallSuite();
        InstallTool(Detection);
        var launcher = Launcher_();
        launcher.Launch(Detection, "MCM", "cm01");
        _starter.Processes[0].HasExited = true;
        Assert.Null(launcher.RunningProcessId(Detection));
        Assert.Equal(SuiteToolLaunchOutcome.Started, launcher.Launch(Detection, "MCM", "cm01").Outcome);
        Assert.Equal(2, _starter.Started.Count);
        Assert.Equal(0, _starter.Processes[0].FrontRequests);
        Assert.Equal(_starter.Processes[1].Id, launcher.RunningProcessId(Detection));
    }

    [Fact]
    public void EachToolHasItsOwnSingleInstance()
    {
        InstallSuite();
        var site = SuiteTools.All.Single(t => t.Id == "site-hygiene");
        InstallTool(Detection);
        InstallTool(site);
        var launcher = Launcher_();
        launcher.Launch(Detection, "", "");
        Assert.Equal(SuiteToolLaunchOutcome.Started, launcher.Launch(site, "", "").Outcome);
        Assert.Equal(2, _starter.Started.Count);
    }

    [Fact]
    public void AMissingToolStartsNothingAndReturnsThePlainMessage()
    {
        InstallSuite();
        var result = Launcher_().Launch(Detection, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Failed, result.Outcome);
        Assert.StartsWith("Detection Tester is not in the AppPackager Suite install.", result.Message);
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public void AMissingWindowsPowerShellStartsNothingAndSaysWhereItLooked()
    {
        _files.Add(Launcher);
        InstallTool(Detection);
        var result = Launcher_().Launch(Detection, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Failed, result.Outcome);
        Assert.Equal($"Detection Tester needs Windows PowerShell 5.1, which was not found at {_environment.WindowsPowerShellPath}.", result.Message);
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public void AStartFailureBecomesAPlainMessageAndNothingIsTracked()
    {
        InstallSuite();
        InstallTool(Detection);
        _starter.Failure = new System.ComponentModel.Win32Exception(5, "Access is denied");
        var launcher = Launcher_();
        var result = launcher.Launch(Detection, "MCM", "cm01");
        Assert.Equal(SuiteToolLaunchOutcome.Failed, result.Outcome);
        Assert.Equal("Detection Tester could not start: Access is denied", result.Message);
        Assert.Null(launcher.RunningProcessId(Detection));
    }

    [Fact]
    public void TheLauncherOnlyReadsTheSuiteSettingsFile()
    {
        InstallSuite(settings: """{ "ToolsRoot": "" }""");
        InstallTool(Detection);
        Launcher_().Launch(Detection, "MCM", "cm01");
        Assert.Equal([Settings], _files.Reads);
    }

    [Fact]
    public void ConstructionTouchesNothing()
    {
        _ = Launcher_();
        Assert.Empty(_files.Reads);
        Assert.Empty(_starter.Started);
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SuiteToolLauncherEnvironmentTests : IDisposable
{
    private readonly string? _site = Environment.GetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable);
    private readonly string? _provider = Environment.GetEnvironmentVariable(SettingsService.LauncherProviderVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, _site);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, _provider);
    }

    private static (SuiteToolLauncher Launcher, FakeToolProcessStarter Starter) InstalledLauncher()
    {
        var files = new FakeSuiteFileSystem();
        var environment = new FakeSuiteEnvironment();
        var starter = new FakeToolProcessStarter();
        var root = Path.Combine(environment.LocalApplicationData, "AppPackagerSuite");
        files.Add(Path.Combine(root, "app-packager-suite", "start-suite.ps1"))
            .Add(environment.WindowsPowerShellPath)
            .Add(Path.Combine(root, "detection-tester", "start-detectiontester.ps1"));
        return (new SuiteToolLauncher(files, environment, starter), starter);
    }

    private static readonly SuiteTool Detection = SuiteTools.All.Single(t => t.Id == "detection-tester");

    [Fact]
    public void ASiteCodeTheOperatorNeverSavedIsNotPassed()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        var (launcher, starter) = InstalledLauncher();
        var settings = new AppSettings { SiteCode = "MCM", SiteCodeSaved = false, ProviderMachine = "cm01.contoso.com" };
        Assert.Equal(SuiteToolLaunchOutcome.Started, launcher.Launch(Detection, settings).Outcome);
        var info = Assert.Single(starter.Started);
        Assert.False(info.Environment.ContainsKey(SettingsService.LauncherSiteCodeVariable));
        Assert.Equal("cm01.contoso.com", info.Environment[SettingsService.LauncherProviderVariable]);
    }

    [Fact]
    public void ASavedSiteCodeIsPassedAndAnEmptyProviderIsNot()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, "old.contoso.com");
        var (launcher, starter) = InstalledLauncher();
        launcher.Launch(Detection, new AppSettings { SiteCode = "LAB", SiteCodeSaved = true, ProviderMachine = " " });
        var info = Assert.Single(starter.Started);
        Assert.Equal("LAB", info.Environment[SettingsService.LauncherSiteCodeVariable]);
        Assert.False(info.Environment.ContainsKey(SettingsService.LauncherProviderVariable));
    }

    [Fact]
    public void AnUnsavedSiteCodeKeepsTheValueTheSuiteLauncherGaveTheDesktop()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "SUI");
        var (launcher, starter) = InstalledLauncher();
        launcher.Launch(Detection, new AppSettings { SiteCode = "SUI", SiteCodeSaved = false });
        Assert.Equal("SUI", Assert.Single(starter.Started).Environment[SettingsService.LauncherSiteCodeVariable]);
    }

    [Fact]
    public void ASavedSiteCodeReplacesTheValueTheSuiteLauncherGaveTheDesktop()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "SUI");
        var (launcher, starter) = InstalledLauncher();
        launcher.Launch(Detection, new AppSettings { SiteCode = "LAB", SiteCodeSaved = true });
        Assert.Equal("LAB", Assert.Single(starter.Started).Environment[SettingsService.LauncherSiteCodeVariable]);
    }

    [Fact]
    public void EmptyDesktopValuesRemoveTheVariablesTheDesktopInherited()
    {
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "OLD");
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, "old.contoso.com");
        var launcher = new SuiteToolLauncher(new FakeSuiteFileSystem(), new FakeSuiteEnvironment(), new FakeToolProcessStarter());
        var info = launcher.BuildStartInfo(new SuiteToolLocation(@"C:\S\t", @"C:\S\t\start.ps1"), " ", null);
        Assert.False(info.Environment.ContainsKey(SettingsService.LauncherSiteCodeVariable));
        Assert.False(info.Environment.ContainsKey(SettingsService.LauncherProviderVariable));
    }
}

public sealed class SuiteToolRegistryTests
{
    private static readonly string[] ToolIds =
    [
        "detection-tester", "maintenance-windows", "collection-and-compliance", "installer-analysis",
        "deployment-helper", "dp-content-manager", "pivots-and-scripts", "site-hygiene"
    ];

    [Fact]
    public void TheEightToolEntriesFollowTheWorkbenchInTheDefaultOrder()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.Equal([SidebarIds.Packages, SidebarIds.Applications, SidebarIds.AddApp, SidebarIds.Workbench, .. ToolIds], SidebarFixtures.Ids(registry.Items));
        Assert.Equal(
        [
            "Detection Tester", "Maintenance Windows", "Collection and Compliance", "Installer Analysis",
            "Deployment Helper", "DP Content Manager", "Pivots and Scripts", "Site Hygiene"
        ], registry.Items.Skip(4).Select(i => i.Title));
    }

    [Fact]
    public void EveryToolEntryIsAMovableToolWithAnAction()
    {
        foreach (var item in SidebarRegistry.CreateDefault().Items.Skip(4))
        {
            Assert.Equal(SidebarItemKind.Tool, item.Kind);
            Assert.False(item.IsPinned);
            Assert.NotNull(item.Activate);
            Assert.Null(item.PageType);
        }
    }

    [Fact]
    public void OnlyPackagesIsPinned()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.Equal([SidebarIds.Packages], registry.Items.Where(i => i.IsPinned).Select(i => i.Id));
        Assert.Equal(SidebarIds.Packages, registry.Items[0].Id);
    }

    [Fact]
    public void EveryEntryHasAUniqueIdTitleAndExistingIcon()
    {
        var items = SidebarRegistry.CreateDefault().Items;
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(items.Count, items.Select(i => i.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(items.Count, items.Select(i => i.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.All(items, i => Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(i.Symbol, out _), i.Symbol));
    }

    [Fact]
    public void ToolEntriesMoveHideAndShowThroughTheService()
    {
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore());
        Assert.True(service.Hide("site-hygiene"));
        Assert.DoesNotContain(service.Items, i => i.Id == "site-hygiene");
        Assert.True(service.Show("site-hygiene"));
        Assert.Equal("site-hygiene", service.Items[^1].Id);
        Assert.True(service.Move("detection-tester", 1));
        Assert.Equal("detection-tester", service.Items[1].Id);
        Assert.Equal(SidebarIds.Packages, service.Items[0].Id);
    }

    [Fact]
    public void ASavedLayoutFromBeforeTheToolsAppendsThemShownAtTheEnd()
    {
        var layout = new SidebarLayout { Order = [SidebarIds.Packages, SidebarIds.Workbench, SidebarIds.Applications, SidebarIds.AddApp], Hidden = [SidebarIds.AddApp] };
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(layout));
        Assert.Equal([SidebarIds.Packages, SidebarIds.Workbench, SidebarIds.Applications, .. ToolIds], SidebarFixtures.Ids(service.Items));
    }

    [Fact]
    public void EachToolEntryMatchesItsSuiteTool()
    {
        var registry = SidebarRegistry.CreateDefault();
        foreach (var tool in SuiteTools.All)
        {
            var item = registry.Find(tool.Id)!;
            Assert.Equal(tool.Title, item.Title);
            Assert.Equal(tool.Symbol, item.Symbol);
        }
    }
}
