using System.Net;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Tableau serves one file under two paths: esdalt and tssoftware. The Tableau definitions download from esdalt and try tssoftware when the
/// first address fails for any reason: a refused or missing file, a connection failure, a body that is no installer. The stage fails only
/// when both fail, and the message lists both reasons. A definition with one address downloads exactly as before. The Prep definition stays
/// on the version that the Prep page publishes. No request goes to Tableau; a local server plays the host.
/// </summary>
public sealed class TableauDownloadFallbackTests : IDisposable
{
    private const string First = "https://downloads.tableau.com/esdalt/tableau_prep/2026.3.0/TableauPrep-2026-3-0.exe";
    private const string Second = "https://downloads.tableau.com/tssoftware/TableauPrep-2026-3-0.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tableau-fallback-" + Guid.NewGuid().ToString("N"));
    private HttpListener? _listener;
    private readonly List<string> _requests = [];

    public TableauDownloadFallbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch (ObjectDisposedException) { }
        TestCleanup.DeleteDirectory(_dir);
    }

    // ---- the address loop --------------------------------------------------------------------------

    private static Func<string, Task> Download(Dictionary<string, string?> outcome, List<string> asked) => url =>
    {
        asked.Add(url);
        return outcome[url] is { } failure ? Task.FromException(new InvalidOperationException(failure)) : Task.CompletedTask;
    };

    [Fact]
    public async Task TheFirstAddressIsUsedWhenItWorksAndTheSecondIsNeverAsked()
    {
        var asked = new List<string>();
        var url = await DownloadAddresses.TryInOrderAsync([First, Second], Download(new() { [First] = null, [Second] = null }, asked));

        Assert.Equal(First, url);
        Assert.Equal([First], asked);
    }

    [Theory]
    [InlineData("Download failed after 2 attempts: 403")]
    [InlineData("curl: (22) The requested URL returned error: 404")]
    [InlineData("curl: (22) The requested URL returned error: 500")]
    [InlineData("curl: (7) Failed to connect to downloads.tableau.com port 443")]
    [InlineData("The downloaded file is not an installer: it starts with <html>")]
    public async Task TheSecondAddressIsUsedAfterAnyFailureOfTheFirst_AndTheLogNamesTheReason(string reason)
    {
        var asked = new List<string>();
        var log = new List<string>();
        var url = await DownloadAddresses.TryInOrderAsync([First, Second], Download(new() { [First] = reason, [Second] = null }, asked), log.Add);

        Assert.Equal(Second, url);
        Assert.Equal([First, Second], asked);
        Assert.Contains(log, l => l.StartsWith("Address skipped:", StringComparison.Ordinal) && l.Contains("esdalt") && l.Contains(reason));
        Assert.Contains(log, l => l.StartsWith("Download address used:", StringComparison.Ordinal) && l.Contains("tssoftware"));
    }

    [Fact]
    public async Task WhenBothFailTheMessageListsBothAddressesWithBothReasons()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DownloadAddresses.TryInOrderAsync([First, Second], Download(new() { [First] = "error 403", [Second] = "error 404" }, [])));

        Assert.StartsWith("The installer could not be downloaded from any of its addresses.", ex.Message);
        Assert.Contains("esdalt/tableau_prep/2026.3.0/TableauPrep-2026-3-0.exe: error 403", ex.Message);
        Assert.Contains("tssoftware/TableauPrep-2026-3-0.exe: error 404", ex.Message);
    }

    [Fact]
    public async Task WithOneAddressTheDownloadRunsOnceAndItsOwnExceptionPassesThrough()
    {
        var asked = new List<string>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DownloadAddresses.TryInOrderAsync([First], Download(new() { [First] = "Download failed after 2 attempts: x" }, asked)));

        Assert.Equal("Download failed after 2 attempts: x", ex.Message);
        Assert.Equal([First], asked);
    }

    [Fact]
    public async Task ACancellationIsNeverAReasonToTryTheNextAddress()
    {
        var asked = new List<string>();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DownloadAddresses.TryInOrderAsync([First, Second], url => { asked.Add(url); throw new OperationCanceledException(); }));

        Assert.Equal([First], asked);
    }

    // ---- the definitions ----------------------------------------------------------------------------

    [Theory]
    [InlineData("tableaudesktop.json", "TableauDesktop-64bit", "https://downloads.tableau.com/esdalt/2026.3.0/TableauDesktop-64bit-2026-3-0.exe")]
    [InlineData("tableaureader.json", "TableauReader-64bit", "https://downloads.tableau.com/esdalt/2026.3.0/TableauReader-64bit-2026-3-0.exe")]
    [InlineData("tableauprep.json", "TableauPrep", First)]
    public void EveryTableauDefinitionDownloadsFromEsdaltFirstAndTssoftwareSecond(string file, string name, string expectedFirst)
    {
        var def = DefinitionFixture.Load(file);
        static string Fill(string template) => StagingService.ApplyDashedVersion(template.Replace("{display_version}", "2026.3.0"), "2026.3.0");

        Assert.Equal(expectedFirst, Fill(def.DownloadUrl));
        Assert.Equal($"https://downloads.tableau.com/tssoftware/{name}-2026-3-0.exe", Fill(def.FallbackDownloadUrl!));
        Assert.True(def.RequireValidSignature);
    }

    [Fact]
    public void ThePrepDefinitionStaysOnTheVersionThatThePrepPagePublishes()
    {
        var def = DefinitionFixture.Load("tableauprep.json");
        const string prepPage = "<p>Current release: 2026.3</p><ul><li>2026.2</li><li>2026.1</li></ul>";

        Assert.Equal("HttpRegex", def.VersionScraper.Type);
        Assert.Equal("https://www.tableau.com/support/releases/prep", def.VersionScraper.Url);
        Assert.Equal("2026.3", HttpRegexScraper.ExtractVersion(prepPage, def.VersionScraper.Pattern, def.VersionScraper.SelectHighestVersion));
        Assert.Null(def.VersionScraper.Hops);
    }

    [Fact]
    public void ADefinitionWithoutAFallbackDownloadsFromItsOneAddress() =>
        Assert.True(string.IsNullOrWhiteSpace(DefinitionFixture.Load("7zip.json").FallbackDownloadUrl));

    // ---- the stage against a local host ---------------------------------------------------------------

    private string StartHost()
    {
        var port = Random.Shared.Next(20000, 40000);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        // The installer is a real PE file with version information: this test assembly's own engine DLL.
        var installer = File.ReadAllBytes(typeof(StagingService).Assembly.Location);
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { break; }
                var path = ctx.Request.Url!.AbsolutePath;
                lock (_requests) _requests.Add(path);
                try
                {
                    if (path.StartsWith("/forbidden/", StringComparison.Ordinal)) ctx.Response.StatusCode = 403;
                    else if (path.StartsWith("/signin/", StringComparison.Ordinal))
                    {
                        ctx.Response.ContentType = "text/html";
                        var page = "<html><body>Sign in</body></html>"u8.ToArray();
                        await ctx.Response.OutputStream.WriteAsync(page);
                    }
                    else if (path.StartsWith("/good/", StringComparison.Ordinal)) await ctx.Response.OutputStream.WriteAsync(installer);
                    else ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        });
        return $"http://localhost:{port}";
    }

    private static AppDefinition LocalDefinition(string first, string? second)
    {
        var def = DefinitionFixture.Load("tableauprep.json");
        def.DownloadUrl = first;
        def.FallbackDownloadUrl = second;
        def.RequireValidSignature = false;
        def.VersionScraper.VersionTransform = null;
        return def;
    }

    private static StagingService Staging() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(), new WrapperGeneratorService(), new ManifestService());

    private static string[] Layout(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).Order().ToArray();

    [Fact]
    public async Task AStageWithA403OnTheFirstAddressDownloadsFromTheSecondAndLeavesTheSameLayoutAsAStageFromOneAddress()
    {
        var host = StartHost();
        var log = new List<string>();

        var withFallback = Path.Combine(_dir, "fallback");
        var result = await Staging().StageAsync(LocalDefinition($"{host}/forbidden/TableauPrep-{{display_version_dashed}}.exe", $"{host}/good/TableauPrep-{{display_version_dashed}}.exe"),
            withFallback, log.Add, knownVersion: "2026.3.0");
        Assert.True(result.Success, result.ErrorMessage);

        var single = Path.Combine(_dir, "single");
        var reference = await Staging().StageAsync(LocalDefinition($"{host}/good/TableauPrep-{{display_version_dashed}}.exe", null), single, null, knownVersion: "2026.3.0");
        Assert.True(reference.Success, reference.ErrorMessage);

        Assert.Equal(Layout(single), Layout(withFallback));
        Assert.Contains(log, l => l.StartsWith("Address skipped:", StringComparison.Ordinal) && l.Contains("/forbidden/"));
        Assert.Contains(log, l => l.StartsWith("Download address used:", StringComparison.Ordinal) && l.Contains("/good/"));
    }

    [Fact]
    public async Task AStageWhoseFirstAddressGivesASignInPageDownloadsFromTheSecondAndKeepsNoPage()
    {
        var host = StartHost();
        var root = Path.Combine(_dir, "signin");

        var result = await Staging().StageAsync(LocalDefinition($"{host}/signin/TableauPrep-{{display_version_dashed}}.exe", $"{host}/good/TableauPrep-{{display_version_dashed}}.exe"),
            root, null, knownVersion: "2026.3.0");

        Assert.True(result.Success, result.ErrorMessage);
        var exe = Directory.GetFiles(root, "TableauPrep-2026-3-0.exe", SearchOption.AllDirectories).First();
        Assert.Equal((byte)'M', File.ReadAllBytes(exe)[0]);
    }

    [Fact]
    public async Task AStageWhoseTwoAddressesBothFailStopsWithBothReasonsAndLeavesNoStage()
    {
        var host = StartHost();
        var root = Path.Combine(_dir, "bothfail");

        var result = await Staging().StageAsync(LocalDefinition($"{host}/forbidden/TableauPrep-{{display_version_dashed}}.exe", $"{host}/missing/TableauPrep-{{display_version_dashed}}.exe"),
            root, null, knownVersion: "2026.3.0");

        Assert.False(result.Success);
        Assert.Contains("could not be downloaded from any of its addresses", result.ErrorMessage);
        Assert.Contains("/forbidden/", result.ErrorMessage);
        Assert.Contains("/missing/", result.ErrorMessage);
    }

    [Fact]
    public async Task AStageWhoseFirstAddressWorksNeverAsksTheSecond()
    {
        var host = StartHost();
        var root = Path.Combine(_dir, "first");

        var result = await Staging().StageAsync(LocalDefinition($"{host}/good/TableauPrep-{{display_version_dashed}}.exe", $"{host}/missing/TableauPrep-{{display_version_dashed}}.exe"),
            root, null, knownVersion: "2026.3.0");

        Assert.True(result.Success, result.ErrorMessage);
        lock (_requests) Assert.DoesNotContain(_requests, r => r.StartsWith("/missing/", StringComparison.Ordinal));
    }
}
