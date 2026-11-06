using System.Net;
using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Services.Update;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Update;

/// <summary>The update window never starts an installer without the Install click, and refuses a changed file.</summary>
public sealed class UpdateViewModelTests : IDisposable
{
    private const string Base = "https://github.com/jasonulbright/app-packager-desktop/releases/download/v2026.1104.1/";
    private const string SetupName = "AppPackagerDesktop-Setup-2026.1104.1.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-updvm-" + Guid.NewGuid().ToString("N"));

    public UpdateViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"The test folder '{_root}' was not deleted: {ex.Message}");
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private sealed class Signature(SignatureInfo info) : ISignatureReader
    {
        public Task<SignatureInfo?> ReadAsync(string path, CancellationToken ct = default) => Task.FromResult<SignatureInfo?>(info);
    }

    private static readonly byte[] Setup = Encoding.UTF8.GetBytes("installer");

    private static UpdateRelease Release() => new("v2026.1104.1", "2026.1104.1", "https://github.com/x/y/releases/tag/v2026.1104.1", "Notes",
    [
        new UpdateAsset(SetupName, Base + SetupName, Setup.Length),
        new UpdateAsset("checksums.txt", Base + "checksums.txt", 107)
    ]);

    private (UpdateViewModel Vm, List<string> Launched, int[] Shutdowns) Create(bool launchWorks = true, string status = "Valid", string? json = null,
        Func<string, Exception?>? fail = null)
    {
        var launched = new List<string>();
        var shutdowns = new int[1];
        var sums = $"{Convert.ToHexStringLower(SHA256.HashData(Setup))}  {SetupName}\n";
        var handler = new Handler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (fail?.Invoke(url) is { } boom) throw boom;
            if (url.StartsWith("https://api.github.com/")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json ?? Json()) };
            return url.EndsWith("checksums.txt")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Setup) };
        });
        var service = new UpdateService(handler, new Signature(new SignatureInfo(status, "CN=Jason Ulbright", true)),
            newFolder: () => Path.Combine(_root, Guid.NewGuid().ToString("N")));
        var vm = new UpdateViewModel(service, "2026.1103.3", path => { launched.Add(path); return launchWorks; }, () => shutdowns[0]++);
        return (vm, launched, shutdowns);
    }

    private static string Json() => $$"""
        {"tag_name":"v2026.1104.1","html_url":"https://github.com/x/y/releases/tag/v2026.1104.1","body":"Notes",
         "assets":[{"name":"{{SetupName}}","browser_download_url":"{{Base}}{{SetupName}}","size":{{Setup.Length}}},
                   {"name":"checksums.txt","browser_download_url":"{{Base}}checksums.txt","size":107}]}
        """;

    [Fact]
    public async Task CheckShowsTheCurrentAndTheNewVersionAndTheNotes()
    {
        var (vm, _, _) = Create();
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateStage.Available, vm.Stage);
        Assert.Equal("2026.1103.3", vm.CurrentVersion);
        Assert.Equal("2026.1104.1", vm.LatestVersion);
        Assert.Equal("Notes", vm.Notes);
        Assert.EndsWith("v2026.1104.1", vm.ReleaseUrl);
        Assert.True(vm.DownloadCommand.CanExecute(null));
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task DownloadVerifiesAndStartsNothing()
    {
        var (vm, launched, shutdowns) = Create();
        await vm.CheckCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(UpdateStage.Ready, vm.Stage);
        Assert.Empty(launched);
        Assert.Equal(0, shutdowns[0]);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task InstallStartsTheInstallerThenClosesTheApp()
    {
        var (vm, launched, shutdowns) = Create();
        await vm.CheckCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        vm.InstallCommand.Execute(null);
        Assert.Single(launched);
        Assert.EndsWith(SetupName, launched[0]);
        Assert.Equal(1, shutdowns[0]);
    }

    [Fact]
    public async Task AFailedLaunchKeepsTheAppOpen()
    {
        var (vm, launched, shutdowns) = Create(launchWorks: false);
        await vm.CheckCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        vm.InstallCommand.Execute(null);
        Assert.Single(launched);
        Assert.Equal(0, shutdowns[0]);
        Assert.Equal(UpdateStage.Problem, vm.Stage);
    }

    [Fact]
    public async Task ABadSignatureIsReportedAndNothingCanInstall()
    {
        var (vm, launched, shutdowns) = Create(status: "HashMismatch");
        await vm.CheckCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(UpdateStage.Problem, vm.Stage);
        Assert.Contains("refused", vm.StatusText);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Empty(launched);
        Assert.Equal(0, shutdowns[0]);
    }

    [Fact]
    public async Task AFileChangedAfterTheDownloadIsNotStarted()
    {
        var (vm, launched, shutdowns) = Create();
        await vm.CheckCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        var file = Directory.EnumerateFiles(_root, SetupName, SearchOption.AllDirectories).Single();
        File.WriteAllText(file, "swapped");
        vm.InstallCommand.Execute(null);
        Assert.Empty(launched);
        Assert.Equal(0, shutdowns[0]);
        Assert.Equal(UpdateStage.Problem, vm.Stage);
    }

    [Fact]
    public async Task ARunningVersionAtTheLatestShowsUpToDate()
    {
        var (vm, _, _) = Create(json: Json().Replace("2026.1104.1", "2026.1103.3"));
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateStage.UpToDate, vm.Stage);
        Assert.False(vm.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnUnexpectedFailureOfTheCheckLeavesTheWindowFree()
    {
        var (vm, _, _) = Create(fail: _ => new InvalidOperationException("boom"));
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateStage.Problem, vm.Stage);
        Assert.Contains("boom", vm.StatusText);
        Assert.True(vm.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task ATimeoutOfTheDownloadIsAFailureNotACancel()
    {
        var (vm, _, _) = Create(fail: url => url.EndsWith(SetupName) ? new TaskCanceledException("timed out") : null);
        await vm.CheckCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(UpdateStage.Problem, vm.Stage);
        Assert.DoesNotContain("cancelled", vm.StatusText);
    }

    [Fact]
    public void TheStartupCheckIsOnByDefault() => Assert.True(new AppSettings().CheckForUpdatesAtStartup);

    [Fact]
    public void TheStartupSettingSurvivesTheJsonRoundTrip()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { CheckForUpdatesAtStartup = false });
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.CheckForUpdatesAtStartup);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.CheckForUpdatesAtStartup);
    }
}
