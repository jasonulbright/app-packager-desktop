using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.VendorImport;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The import window: fetch and preview write nothing, Import needs the confirmations, Remove feed asks first.</summary>
public sealed class VendorImportViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-vendor-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return answer(request, cancellationToken);
        }
    }

    private static string Definition(string id, string application = "Contoso Agent", Action<JsonObject>? change = null)
    {
        var def = new AppDefinition
        {
            Id = id, Vendor = "Contoso", Application = application, CMName = application, Architecture = "x64",
            VendorUrl = "https://contoso.example/", InstallerType = "MSI", DownloadUrl = "https://contoso.example/agent-{version}.msi",
            InstallArgs = "/qn", Detection = new DetectionConfig { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\Contoso" }
        };
        var node = JsonSerializer.SerializeToNode(def)!.AsObject();
        change?.Invoke(node);
        return node.ToJsonString();
    }

    private static string Doc(params string[] definitions) =>
        $$"""{"FeedFormat":1,"Vendor":"Contoso Software","FeedId":"contoso-software","Updated":"2026-11-12T08:00:00Z","Definitions":[{{string.Join(",", definitions)}}]}""";

    private static Handler Serves(string body) => new((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    private VendorImportViewModel Create(Handler handler, Func<string, string, Task<bool>>? confirm = null, params string[] shipped)
    {
        var service = new VendorImportService(
            new ImportedDefinitionStore(_root),
            () => new HashSet<string>(shipped, StringComparer.OrdinalIgnoreCase),
            () => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new FeedFetcher(() => handler, null, (_, _) => Task.CompletedTask));
        return new VendorImportViewModel(service, confirm ?? ((_, _) => Task.FromResult(true)));
    }

    [Fact]
    public async Task FetchShowsThePreviewAndWritesNothing()
    {
        var vm = Create(Serves(Doc(Definition("contoso-agent"))));
        vm.Address = "https://vendor.example/feed.json";

        await vm.FetchCommand.ExecuteAsync(null);

        Assert.True(vm.HasPreview);
        Assert.Equal("contoso-agent", Assert.Single(vm.Rows).Id);
        Assert.Equal("Ready", vm.Rows[0].Status);
        Assert.Equal("Contoso Software", vm.Vendor);
        Assert.Equal("contoso-software", vm.FeedId);
        Assert.Equal("Definition document NOT verified", vm.TrustLabel);
        Assert.Contains("Definition document NOT verified", vm.SourceText);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("http://vendor.example/feed.json")]
    [InlineData("file:///C:/feed.json")]
    [InlineData("not an address")]
    public async Task AnAddressThatIsNotHttpsIsRefusedWithoutARequest(string address)
    {
        var handler = Serves(Doc(Definition("a-1")));
        var vm = Create(handler);
        vm.Address = address;

        await vm.FetchCommand.ExecuteAsync(null);

        Assert.False(vm.HasPreview);
        Assert.Equal(0, handler.Requests);
        Assert.Contains("https://", vm.StatusText);
    }

    [Fact]
    public async Task ADocumentThatIsNotVerifiedNeedsItsConfirmationBeforeImportIsAvailable()
    {
        var vm = Create(Serves(Doc(Definition("contoso-agent"))));
        vm.Address = "https://vendor.example/feed.json";
        await vm.FetchCommand.ExecuteAsync(null);

        Assert.True(vm.NeedsNotVerifiedConfirmation);
        Assert.False(vm.ImportCommand.CanExecute(null));

        vm.ConfirmedNotVerified = true;
        Assert.True(vm.ImportCommand.CanExecute(null));
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.True(vm.ImportedAny);
        Assert.False(vm.HasPreview);
        var feed = Assert.Single(vm.Feeds);
        Assert.Equal(("contoso-software", 1), (feed.FeedId, feed.Count));
        Assert.Equal("Definition document NOT verified", feed.Trust);
        Assert.True(File.Exists(Path.Combine(_root, "contoso-software", "contoso-agent.json")));
    }

    [Fact]
    public async Task APinnedDigestThatMatchesNeedsNoNotVerifiedConfirmation()
    {
        var body = Doc(Definition("contoso-agent"));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var vm = Create(Serves(body));
        vm.Address = "https://vendor.example/feed.json";
        vm.PinnedDigest = digest;

        await vm.FetchCommand.ExecuteAsync(null);

        Assert.False(vm.NeedsNotVerifiedConfirmation);
        Assert.True(vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task APinThatDoesNotMatchStopsTheImport()
    {
        var vm = Create(Serves(Doc(Definition("contoso-agent"))));
        vm.Address = "https://vendor.example/feed.json";
        vm.PinnedDigest = new string('0', 64);

        await vm.FetchCommand.ExecuteAsync(null);

        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.Contains("does not fall back", vm.FindingsText);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ADefinitionThatCarriesCodeIsShownAsBlockedAndIsNotImported()
    {
        var vm = Create(Serves(Doc(Definition("contoso-agent"), Definition("code-1", "Code App", n => n["UninstallCommand"] = "msiexec /x {GUID}"))));
        vm.Address = "https://vendor.example/feed.json";
        await vm.FetchCommand.ExecuteAsync(null);
        vm.ConfirmedNotVerified = true;

        Assert.StartsWith("Blocked: carries code", vm.Rows.Single(r => r.Id == "code-1").Status);
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(_root, "contoso-software", "contoso-agent.json")));
        Assert.False(File.Exists(Path.Combine(_root, "contoso-software", "code-1.json")));
    }

    [Fact]
    public async Task UnblockingACommandNeedsAConfirmationThatNamesTheDefinitionAndShowsItsText()
    {
        var asked = new List<(string Title, string Message)>();
        var vm = Create(Serves(Doc(Definition("contoso-agent"), Definition("code-1", "Code App", n => n["UninstallCommand"] = "msiexec /x {GUID}"))),
            (title, message) => { asked.Add((title, message)); return Task.FromResult(true); });
        vm.Address = "https://vendor.example/feed.json";
        await vm.FetchCommand.ExecuteAsync(null);
        vm.ConfirmedNotVerified = true;
        vm.SelectedRow = vm.Rows.Single(r => r.Id == "code-1");

        Assert.True(vm.UnblockSelectedCommand.CanExecute(null));
        await vm.UnblockSelectedCommand.ExecuteAsync(null);

        var (title, message) = Assert.Single(asked);
        Assert.Contains("code-1", title);
        Assert.Contains("Code App", message);
        Assert.Contains("msiexec /x {GUID}", message);
        Assert.Contains("SHA-256", message);
        Assert.StartsWith("Unblocked", vm.Rows.Single(r => r.Id == "code-1").Status);
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(_root, "contoso-software", "code-1.json")));
    }

    [Fact]
    public async Task ADeclinedUnblockKeepsTheDefinitionBlockedAndAPathToALocalFileOffersNoUnblock()
    {
        var vm = Create(Serves(Doc(Definition("contoso-agent"),
                Definition("code-1", "Code App", n => n["UninstallCommand"] = "msiexec /x {GUID}"),
                Definition("local-1", "Local App", n => n["LocalInstaller"] = new JsonObject()))),
            (_, _) => Task.FromResult(false));
        vm.Address = "https://vendor.example/feed.json";
        await vm.FetchCommand.ExecuteAsync(null);
        vm.ConfirmedNotVerified = true;

        vm.SelectedRow = vm.Rows.Single(r => r.Id == "code-1");
        await vm.UnblockSelectedCommand.ExecuteAsync(null);
        Assert.StartsWith("Blocked", vm.Rows.Single(r => r.Id == "code-1").Status);

        vm.SelectedRow = vm.Rows.Single(r => r.Id == "local-1");
        Assert.False(vm.UnblockSelectedCommand.CanExecute(null));
        Assert.Contains("never unblocked", vm.Rows.Single(r => r.Id == "local-1").Status);
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(_root, "contoso-software", "code-1.json")));
        Assert.False(File.Exists(Path.Combine(_root, "contoso-software", "local-1.json")));
    }

    [Fact]
    public async Task AShippedIdIsSkippedUnlessTheAdministratorChoosesToReplaceIt()
    {
        var asked = new List<string>();
        var vm = Create(Serves(Doc(Definition("shared-1"), Definition("new-1", "Other Tool"))),
            (title, message) => { asked.Add(message); return Task.FromResult(true); }, "shared-1");
        vm.Address = "https://vendor.example/feed.json";
        await vm.FetchCommand.ExecuteAsync(null);
        vm.ConfirmedNotVerified = true;

        Assert.Contains("skipped unless replaced", vm.Rows.Single(r => r.Id == "shared-1").Status);
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Empty(asked);
        Assert.Equal(["new-1"], new ImportedDefinitionStore(_root).ReadSource("contoso-software")!.Ids);

        // Another feed id, replacing the shipped definition: the administrator is asked and names the Id.
        vm.Address = "https://vendor.example/feed.json";
        await vm.FetchCommand.ExecuteAsync(null);
        vm.FeedId = "contoso-two";
        vm.ConfirmedNotVerified = true;
        vm.Rows.Single(r => r.Id == "shared-1").Replace = true;
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Contains("shared-1", Assert.Single(asked));
        Assert.Contains("shared-1", new ImportedDefinitionStore(_root).ReadSource("contoso-two")!.ReplacedShippedIds);
    }

    [Fact]
    public async Task DecliningTheReplacementWritesNothing()
    {
        var vm = Create(Serves(Doc(Definition("shared-1"))), (_, _) => Task.FromResult(false), "shared-1");
        vm.Address = "https://vendor.example/feed.json";
        await vm.FetchCommand.ExecuteAsync(null);
        vm.ConfirmedNotVerified = true;
        vm.Rows[0].Replace = true;

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.False(vm.ImportedAny);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CancellingAFetchWritesNothing()
    {
        var started = new TaskCompletionSource();
        var handler = new Handler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var vm = Create(handler);
        vm.Address = "https://vendor.example/feed.json";

        var fetch = vm.FetchCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.CancelFetchCommand.CanExecute(null));
        vm.CancelFetchCommand.Execute(null);
        await fetch;

        Assert.StartsWith("Cancelled", vm.StatusText);
        Assert.False(vm.Busy);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task RemoveFeedAsksFirstAndKeepsTheFeedWhenDeclined()
    {
        var accept = false;
        string? asked = null;
        var vm = Create(Serves(Doc(Definition("contoso-agent"), Definition("two-1", "Two"))),
            (_, message) => { asked = message; return Task.FromResult(accept); });
        vm.Address = "https://vendor.example/feed.json";
        await vm.FetchCommand.ExecuteAsync(null);
        vm.ConfirmedNotVerified = true;
        await vm.ImportCommand.ExecuteAsync(null);

        vm.SelectedFeed = vm.Feeds.Single();
        await vm.RemoveFeedCommand.ExecuteAsync(null);
        Assert.Contains("2 definition(s)", asked);
        Assert.Single(vm.Feeds);

        accept = true;
        await vm.RemoveFeedCommand.ExecuteAsync(null);

        Assert.Empty(vm.Feeds);
        Assert.False(Directory.Exists(Path.Combine(_root, "contoso-software")));
    }

    [Fact]
    public async Task AQueryOfTheAddressIsNeverShown()
    {
        var vm = Create(Serves(Doc(Definition("contoso-agent"))));
        vm.Address = "https://vendor.example/feed.json?v=SECRETQUERY";

        await vm.FetchCommand.ExecuteAsync(null);

        Assert.DoesNotContain("SECRETQUERY", vm.SourceText + vm.StatusText + vm.FindingsText);
    }
}
