using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-26: a staging folder that other accounts can write to. Settings warns when the folder is set, the stage result and the run status carry the same text,
/// a safe folder is one action away, and the folder can be allowed anyway only with a recorded acknowledgement that ends when the folder or its access list changes.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class StagingSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-stage-safety-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedDataRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public StagingSafetyTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, Path.Combine(_root, "data"));
        Directory.CreateDirectory(AppDataPaths.Root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedDataRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void Grant(string folder, WellKnownSidType account, FileSystemRights rights)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(account, null), rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private string OpenFolder(string name = "open")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        Grant(folder, WellKnownSidType.AuthenticatedUserSid, FileSystemRights.Modify);
        return folder;
    }

    // ---- the check and its text

    [Fact]
    public void AFolderWithAnExtraWriterIsBlockedAndTheTextNamesTheFolderTheAccountsTheReasonAndTheFix()
    {
        var folder = OpenFolder();

        var status = StagingRootGuard.Check(folder);

        Assert.Equal(StagingRootGuard.FolderState.Blocked, status.State);
        Assert.True(status.StopsStaging);
        Assert.Contains(folder, status.Text);
        Assert.Contains("other accounts", status.Text);
        Assert.Contains("blocked for security", status.Text);
        Assert.Contains(StagingRootGuard.SafeFolder, status.Text);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Translate(typeof(NTAccount)).Value, status.Text);
        Assert.True(StagingRootGuard.IsBlockedText(status.Text));
        Assert.DoesNotContain('\n', status.Text);
    }

    [Fact]
    public void ASafeAMissingAndAnEmptyFolderGiveNoWarning()
    {
        var safe = Path.Combine(_root, "safe");
        StagingRootGuard.Prepare(safe);

        Assert.Equal(StagingRootGuard.FolderState.Safe, StagingRootGuard.Check(safe).State);
        Assert.Equal(StagingRootGuard.FolderState.Missing, StagingRootGuard.Check(Path.Combine(_root, "absent")).State);
        Assert.Equal(StagingRootGuard.FolderState.NotSet, StagingRootGuard.Check("  ").State);
        Assert.Equal(StagingRootGuard.FolderState.NotSet, StagingRootGuard.Check(null).State);
        Assert.False(StagingRootGuard.Check(safe).StopsStaging);
    }

    [Fact]
    public void TheRefusalOfAStageIsTheSameTextAsTheSettingsWarning()
    {
        var folder = OpenFolder();
        var warning = StagingRootGuard.Check(folder).Text;

        var prepare = Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Prepare(folder));
        var verify = Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Verify(folder));

        Assert.Equal(warning, prepare.Message);
        Assert.Equal(warning, verify.Message);
    }

    [Fact]
    public void TheStageNowStatusAndTheRunStatusCarryTheTextOfTheBlock()
    {
        var text = StagingRootGuard.Check(OpenFolder()).Text;

        var stageNow = PackagesViewModel.StageNowStatus("Stage failed.", text);
        var again = PackagesViewModel.StageNowStatus("Stage: 0 of 1 staged. " + text, text);

        Assert.Contains(text, stageNow);
        Assert.Equal("Stage: 0 of 1 staged. " + text, again);
    }

    [Fact]
    public async Task AStageInABlockedFolderFailsWithThatTextInTheResult()
    {
        var folder = OpenFolder();
        var service = new StagingService(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(), new WrapperGeneratorService(), new ManifestService());
        var manifest = new ApplicationPackagerEngine.Models.StageManifest { AppName = "Contoso", SoftwareVersion = "1.0", InstallerFile = "setup.exe" };
        var installer = Path.Combine(_root, "setup.exe");
        await File.WriteAllTextAsync(installer, "x");

        var result = await service.StageDroppedInstallerAsync(manifest, installer, folder);

        Assert.False(result.Success);
        Assert.Equal(StagingRootGuard.Check(folder).Text, result.ErrorMessage);
        Assert.Null(result.StagingNote);
    }

    // ---- the allowance

    [Fact]
    public void ABlockedFolderStaysBlockedByDefaultAndNothingIsStored()
    {
        var folder = OpenFolder();

        Assert.Equal(StagingRootGuard.FolderState.Blocked, StagingRootGuard.Check(folder).State);
        Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Verify(folder));
        Assert.Empty(StagingAllowanceStore.All());
        Assert.Null(StagingRootGuard.AllowanceNote(folder));
    }

    [Fact]
    public void TheAllowanceNeedsTheAcknowledgement()
    {
        var folder = OpenFolder();

        Assert.Throws<InvalidOperationException>(() => StagingAllowanceStore.Allow(folder, acknowledged: false));
        Assert.Empty(StagingAllowanceStore.All());
        Assert.Throws<DirectoryNotFoundException>(() => StagingAllowanceStore.Allow(Path.Combine(_root, "absent"), acknowledged: true));
    }

    [Fact]
    public void AnAcknowledgedFolderPassesAndTheRecordHasTheFolderTheUserAndTheDate()
    {
        var folder = OpenFolder();
        var at = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        var record = StagingAllowanceStore.Allow(folder, acknowledged: true, at);

        Assert.Equal(StagingRootGuard.FolderState.Allowed, StagingRootGuard.Check(folder).State);
        Assert.False(StagingRootGuard.Check(folder).StopsStaging);
        StagingRootGuard.Verify(folder);
        Assert.Equal(folder, StagingRootGuard.Prepare(folder));
        Assert.Equal(folder, record.Folder);
        Assert.Equal(WindowsIdentity.GetCurrent().Name, record.User);
        Assert.Equal(at, record.AcknowledgedAt);
        Assert.NotEmpty(record.AccessFingerprint);
        Assert.StartsWith(StagingRootGuard.AllowedNote, StagingRootGuard.AllowanceNote(folder));
        Assert.Contains(WindowsIdentity.GetCurrent().Name, StagingRootGuard.AllowanceNote(folder));
        Assert.Contains("2026-11-16", StagingRootGuard.AllowanceNote(folder));
        Assert.Contains("staging allowed by user acknowledgement", File.ReadAllText(Path.Combine(AppDataPaths.Root, StagingAllowanceStore.FileName)) + StagingRootGuard.AllowedNote);
    }

    [Fact]
    public void TheAllowanceCoversTheContentBelowTheFolderButNotAnotherFolder()
    {
        var folder = OpenFolder();
        var other = OpenFolder("other");
        StagingAllowanceStore.Allow(folder, acknowledged: true);
        var content = Directory.CreateDirectory(Path.Combine(folder, "app", "1.0")).FullName;

        StagingRootGuard.Verify(content);

        Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Verify(other));
        Assert.Equal(StagingRootGuard.FolderState.Blocked, StagingRootGuard.Check(other).State);
    }

    [Fact]
    public async Task AStageInAnAllowedFolderRunsPastTheGuardAndTheResultStatesTheAllowance()
    {
        var folder = OpenFolder();
        StagingAllowanceStore.Allow(folder, acknowledged: true);
        var service = new StagingService(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(), new WrapperGeneratorService(), new ManifestService());
        var manifest = new ApplicationPackagerEngine.Models.StageManifest { AppName = "Contoso", SoftwareVersion = "1.0", InstallerFile = "setup.exe" };
        var installer = Path.Combine(_root, "setup.exe");
        await File.WriteAllTextAsync(installer, "x");
        var log = new List<string>();

        var result = await service.StageDroppedInstallerAsync(manifest, installer, folder, log.Add);

        Assert.DoesNotContain(StagingRootGuard.BlockedPrefix, result.ErrorMessage ?? "");
        Assert.Contains(StagingRootGuard.AllowedNote, result.StagingNote);
        Assert.Contains(log, line => line.StartsWith(StagingRootGuard.AllowedNote));
    }

    [Fact]
    public void TheAllowanceEndsWhenTheAccessListOfTheFolderChanges()
    {
        var folder = OpenFolder();
        StagingAllowanceStore.Allow(folder, acknowledged: true);
        Assert.Equal(StagingRootGuard.FolderState.Allowed, StagingRootGuard.Check(folder).State);

        Grant(folder, WellKnownSidType.BuiltinUsersSid, FileSystemRights.Modify);

        Assert.Equal(StagingRootGuard.FolderState.Blocked, StagingRootGuard.Check(folder).State);
        Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Verify(folder));
        Assert.Empty(StagingAllowanceStore.All());
    }

    [Fact]
    public void TheAllowanceEndsWhenTheStagingFolderChanges()
    {
        var folder = OpenFolder();
        var other = OpenFolder("other");
        StagingAllowanceStore.Allow(folder, acknowledged: true);
        StagingAllowanceStore.Allow(other, acknowledged: true);

        StagingAllowanceStore.RemoveAllExcept(other);

        Assert.Equal(StagingRootGuard.FolderState.Blocked, StagingRootGuard.Check(folder).State);
        Assert.Equal(StagingRootGuard.FolderState.Allowed, StagingRootGuard.Check(other).State);
        StagingAllowanceStore.RemoveAllExcept(Path.Combine(_root, "somewhere-else"));
        Assert.Empty(StagingAllowanceStore.All());
    }

    [Fact]
    public void RevokingTheAllowanceBlocksTheFolderAgain()
    {
        var folder = OpenFolder();
        StagingAllowanceStore.Allow(folder, acknowledged: true);

        StagingAllowanceStore.Revoke(folder);

        Assert.Equal(StagingRootGuard.FolderState.Blocked, StagingRootGuard.Check(folder).State);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("unacknowledged")]
    [InlineData("fingerprint")]
    public void AHandWrittenRecordWithoutTheAcknowledgementOfThisUserAndThisAccessListIsIgnored(string defect)
    {
        var folder = OpenFolder();
        var real = StagingAllowanceStore.Allow(folder, acknowledged: true);
        var forged = defect switch
        {
            "user" => real with { User = @"CONTOSO\someone-else" },
            "unacknowledged" => real with { Acknowledged = false },
            _ => real with { AccessFingerprint = "0000" }
        };
        File.WriteAllText(Path.Combine(AppDataPaths.Root, StagingAllowanceStore.FileName), JsonSerializer.Serialize(new[] { forged }));

        Assert.Equal(StagingRootGuard.FolderState.Blocked, StagingRootGuard.Check(folder).State);
        Assert.Throws<UnauthorizedAccessException>(() => StagingRootGuard.Verify(folder));
    }

    [Fact]
    public void AnUnreadableAllowanceFileAllowsNothing()
    {
        var folder = OpenFolder();
        File.WriteAllText(Path.Combine(AppDataPaths.Root, StagingAllowanceStore.FileName), "{ not json");

        Assert.Equal(StagingRootGuard.FolderState.Blocked, StagingRootGuard.Check(folder).State);
    }

    // ---- Settings

    [Fact]
    public void TheFoldersPageShowsTheWarningTheSafeFolderActionAndTheAllowControl()
    {
        var xaml = File.ReadAllText(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Pages", "SettingsPage.xaml"));
        var folders = xaml[xaml.IndexOf("x:Name=\"page_folders\"", StringComparison.Ordinal)..xaml.IndexOf("x:Name=\"page_dtdefaults\"", StringComparison.Ordinal)];

        Assert.Contains("StagingWarning", folders);
        Assert.Contains("CreateSafeStagingFolderCommand", folders);
        Assert.Contains("Allow this folder anyway", folders);
        Assert.Contains("AllowStagingFolderCommand", folders);
        Assert.Contains("RemoveStagingAllowanceCommand", folders);
    }

    public static TheoryData<ApplicationTheme> Themes() => new() { ApplicationTheme.Light, ApplicationTheme.Dark };

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheWarningFitsTheFoldersPageAndTheAllowanceWindowNeedsTheCheckedBox(ApplicationTheme theme)
    {
        var text = StagingRootGuard.Check(OpenFolder()).Text;
        PageHeaderRenderTests.With("Settings", theme, () =>
        {
            using var host = new PageHeaderRenderTests();
            var (page, _) = host.Create("Settings");
            var settings = (SettingsPage)page;
            settings.DataContext = new FoldersContext
            {
                Sidebar = ((dynamic)settings.DataContext).Sidebar,
                StagingWarning = text,
                ShowStagingWarning = true,
                DownloadRoot = "C:\temp"
            };
            settings.SelectPage("folders");
            var window = new Window { Width = 1920 - 230, Height = 1032 - 48, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var warning = ById<TextBlock>(window, "txtStagingWarning");
                Assert.Equal(text, warning.Text);
                Assert.True(warning.IsVisible);
                Assert.True(ById<Wpf.Ui.Controls.Button>(window, "btnStagingSafeFolder").IsVisible);
                Assert.True(ById<Wpf.Ui.Controls.Button>(window, "btnStagingAllow").IsVisible);
                var scroll = (ScrollViewer)settings.FindName("page_folders");
                Assert.True(scroll.ScrollableHeight < 0.5, "The Folders page is taller than the work area with the warning.");
                Save(window, $"settings-folders-warning-{theme.ToString().ToLowerInvariant()}");
            }
            finally { window.Close(); }
        });

        WithApplication(theme, () =>
        {
            var window = new StagingAllowanceWindow(@"C:\temp", StagingRootGuard.AllowanceReason) { Left = -32000, Top = -32000, ShowInTaskbar = false };
            Show(window);
            try
            {
                Settle(window);
                var accept = ById<Wpf.Ui.Controls.Button>(window, "btnAllowAccept");
                var check = ById<System.Windows.Controls.CheckBox>(window, "chkAllowAcknowledge");
                Assert.False(accept.IsEnabled);
                Assert.True(ById<Wpf.Ui.Controls.Button>(window, "btnAllowCancel").IsDefault);
                Assert.Contains("swap a staged installer or script", ById<TextBlock>(window, "txtAllowReason").Text);
                check.IsChecked = true;
                Settle(window);
                Assert.True(accept.IsEnabled);
                Save(window, $"staging-allowance-window-{theme.ToString().ToLowerInvariant()}");
            }
            finally { window.Close(); }
        });
    }

    /// <summary>The members of the view model that the Folders page binds to, with setters so the two-way boxes bind.</summary>
    private sealed class FoldersContext
    {
        public object? Sidebar { get; set; }
        public string StagingWarning { get; set; } = "";
        public bool ShowStagingWarning { get; set; }
        public string StagingAllowedText { get; set; } = "";
        public bool ShowStagingAllowed { get; set; }
        public string DownloadRoot { get; set; } = "";
        public string PackagersRoot { get; set; } = "";
    }

    private static void Save(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("APD_SETTINGS_SHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }
}
