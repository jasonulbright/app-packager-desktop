using System.Net;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The manual update of a stored feed (step 4): a diff first, nothing written before the confirmation, unblocks that survive only while their code is unchanged.</summary>
public sealed class VendorUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-update-" + Guid.NewGuid().ToString("N"));
    private string _body = "";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class BodyHandler(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "application/json") });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AppDefinition Def(string id, string host = "contoso.example") => new()
    {
        Id = id, Vendor = "Contoso", Application = "Contoso " + id, CMName = "Contoso " + id, Architecture = "x64",
        VendorUrl = "https://contoso.example/", InstallerType = "MSI", DownloadUrl = $"https://{host}/{id}-{{version}}.msi",
        InstallArgs = "/qn", Detection = new DetectionConfig { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\Contoso" }
    };

    private static AppDefinition WithCommand(string id, string command = "msiexec /x {GUID} /qn")
    {
        var d = Def(id);
        d.UninstallCommand = command;
        return d;
    }

    private static string Doc(string updated, params AppDefinition[] definitions) =>
        "{\"FeedFormat\":1,\"Vendor\":\"Contoso Software\",\"FeedId\":\"contoso-software\",\"Updated\":\"" + updated + "\",\"Definitions\":["
        + string.Join(",", definitions.Select(d => JsonSerializer.Serialize(d))) + "]}";

    private (VendorFeedImporter Importer, VendorFeedUpdater Updater, ImportedDefinitionStore Store) Create()
    {
        var store = new ImportedDefinitionStore(_root);
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        var importer = new VendorFeedImporter(new FeedFetcher(() => new BodyHandler(() => _body), null, (_, _) => Task.CompletedTask), store,
            () => new HashSet<string>(StringComparer.OrdinalIgnoreCase), () => new HashSet<string>(StringComparer.OrdinalIgnoreCase), clock);
        return (importer, new VendorFeedUpdater(importer, store, clock), store);
    }

    private static readonly Uri Address = new("https://vendor.example/feed.json");

    private async Task<(VendorFeedUpdater Updater, ImportedDefinitionStore Store)> ImportedAsync(string updated, params AppDefinition[] definitions)
    {
        var (importer, updater, store) = Create();
        _body = Doc(updated, definitions);
        var preview = await importer.PreviewAsync(Address, null, CancellationToken.None);
        importer.Import(preview, new ImportConfirmation("Contoso Software", "contoso-software", true, new HashSet<string>(),
            new HashSet<string>(definitions.Where(d => d.UninstallCommand is not null).Select(d => d.Id), StringComparer.OrdinalIgnoreCase)));
        return (updater, store);
    }

    private static UpdateConfirmation Confirm(bool older = false, string[]? remove = null, string[]? unblock = null) =>
        new(true, older, new HashSet<string>(remove ?? [], StringComparer.OrdinalIgnoreCase), new HashSet<string>(), new HashSet<string>(unblock ?? [], StringComparer.OrdinalIgnoreCase));

    private static string FileOf(ImportedDefinitionStore store, string id) => Path.Combine(store.Root, "contoso-software", id + ".json");

    [Fact]
    public async Task ThePreviewShowsWhatWasAddedRemovedAndChangedAndWritesNothing()
    {
        var (updater, store) = await ImportedAsync("2026-11-12T08:00:00Z", Def("app-a"), Def("app-b"), Def("app-c"));
        var before = File.ReadAllText(FileOf(store, "app-a"));
        var changed = Def("app-a");
        changed.InstallArgs = "/qn /norestart";
        _body = Doc("2026-11-13T08:00:00Z", changed, Def("app-b"), Def("app-d"));

        var update = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);

        Assert.Equal(DefinitionChange.Changed, update.Diffs.Single(d => d.Id == "app-a").Change);
        Assert.Contains("InstallArgs", update.Diffs.Single(d => d.Id == "app-a").ChangedProperties);
        Assert.Equal(DefinitionChange.Unchanged, update.Diffs.Single(d => d.Id == "app-b").Change);
        Assert.Equal(DefinitionChange.Removed, update.Diffs.Single(d => d.Id == "app-c").Change);
        Assert.Equal(DefinitionChange.Added, update.Diffs.Single(d => d.Id == "app-d").Change);
        Assert.Equal(before, File.ReadAllText(FileOf(store, "app-a")));
        Assert.True(File.Exists(FileOf(store, "app-c")));
        Assert.False(File.Exists(FileOf(store, "app-d")));
    }

    [Fact]
    public async Task AChangedHostAHashSourceARequirementAndNewCodeNeedAttention()
    {
        var (updater, _) = await ImportedAsync("2026-11-12T08:00:00Z", Def("app-a"), Def("app-b"), Def("app-c"), Def("app-d"));
        var host = Def("app-a", host: "evil.example");
        var hash = Def("app-b");
        hash.PayloadSha256 = new string('a', 64);
        var requirement = Def("app-c");
        requirement.RequirePayloadHash = true;
        _body = Doc("2026-11-13T08:00:00Z", host, hash, requirement, WithCommand("app-d"));

        var update = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);

        Assert.True(update.Diffs.Single(d => d.Id == "app-a").HostChanged);
        Assert.True(update.Diffs.Single(d => d.Id == "app-b").HashSourceChanged);
        Assert.True(update.Diffs.Single(d => d.Id == "app-c").RequirementChanged);
        Assert.True(update.Diffs.Single(d => d.Id == "app-d").CodeChanged);
        Assert.All(update.Diffs, d => Assert.True(d.NeedsAttention));
    }

    [Fact]
    public async Task NothingIsWrittenWithoutTheConfirmationOfAnUnverifiedDocumentAndAnOlderOne()
    {
        var (updater, store) = await ImportedAsync("2026-11-12T08:00:00Z", Def("app-a"));
        _body = Doc("2026-11-01T08:00:00Z", Def("app-a"), Def("app-b"));
        var update = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);

        Assert.True(update.OlderThanStored);
        Assert.Throws<InvalidOperationException>(() => updater.Apply(update, new UpdateConfirmation(false, true, new HashSet<string>(), new HashSet<string>(), new HashSet<string>())));
        Assert.Throws<InvalidOperationException>(() => updater.Apply(update, Confirm(older: false)));
        Assert.False(File.Exists(FileOf(store, "app-b")));

        updater.Apply(update, Confirm(older: true));
        Assert.True(File.Exists(FileOf(store, "app-b")));
    }

    [Fact]
    public async Task ARemovedDefinitionStaysUntilItsRemovalIsConfirmed()
    {
        var (updater, store) = await ImportedAsync("2026-11-12T08:00:00Z", Def("app-a"), Def("app-c"));
        _body = Doc("2026-11-13T08:00:00Z", Def("app-a"));
        var update = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);

        var kept = updater.Apply(update, Confirm());
        Assert.Equal(["app-a", "app-c"], kept.Ids.Order());
        Assert.True(File.Exists(FileOf(store, "app-c")));

        var update2 = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);
        var removed = updater.Apply(update2, Confirm(remove: ["app-c"]));
        Assert.Equal(["app-a"], removed.Ids);
        Assert.False(File.Exists(FileOf(store, "app-c")));
    }

    [Fact]
    public async Task AnUnblockSurvivesAnUpdateThatChangesNothingInItsCodeAndIsLostWhenTheCommandChanges()
    {
        var (updater, store) = await ImportedAsync("2026-11-12T08:00:00Z", WithCommand("app-a"), Def("app-b"));
        var digest = (store.ReadSource("contoso-software")!.UnblockedDigests).Single();

        // Same code, another property: the unblock stays.
        var sameCode = WithCommand("app-a");
        sameCode.VendorUrl = "https://contoso.example/new";
        _body = Doc("2026-11-13T08:00:00Z", sameCode, Def("app-b"));
        var update = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);
        Assert.True(update.Diffs.Single(d => d.Id == "app-a").UnblockKept);
        Assert.Equal([digest], updater.Apply(update, Confirm()).UnblockedDigests);

        // A changed command: the definition is blocked again, the stored one stays, and the diff says so.
        _body = Doc("2026-11-14T08:00:00Z", WithCommand("app-a", "msiexec /x {GUID} /qn && calc"), Def("app-b"));
        var changed = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);
        Assert.True(changed.Diffs.Single(d => d.Id == "app-a").UnblockLost);
        var source = updater.Apply(changed, Confirm());
        Assert.DoesNotContain("calc", File.ReadAllText(FileOf(store, "app-a")));
        Assert.Equal([digest], source.UnblockedDigests);

        // The administrator unblocks the new text: it is stored with its new digest.
        var again = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);
        var newSource = updater.Apply(again, Confirm(unblock: ["app-a"]));
        Assert.Contains("calc", File.ReadAllText(FileOf(store, "app-a")));
        Assert.NotEqual(digest, newSource.UnblockedDigests.Single());
    }

    [Fact]
    public async Task APinnedFeedKeepsItsPinAndAChangedDocumentIsRefusedWithItsNewDigest()
    {
        var (importer, updater, store) = Create();
        _body = Doc("2026-11-12T08:00:00Z", Def("app-a"));
        var first = await importer.PreviewAsync(Address, null, CancellationToken.None);
        var digest = first.Document.Sha256;
        var pinned = await importer.PreviewAsync(Address, digest, CancellationToken.None);
        importer.Import(pinned, new ImportConfirmation("Contoso Software", "contoso-software", false, new HashSet<string>()));
        Assert.Equal(digest, store.ReadSource("contoso-software")!.PinnedDigest);

        _body = Doc("2026-11-13T08:00:00Z", Def("app-a"), Def("app-b"));
        var update = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);

        Assert.True(update.Preview.Refused);
        Assert.Contains(update.Preview.Document.Sha256, update.Preview.Refusal);
        Assert.Throws<InvalidOperationException>(() => updater.Apply(update, Confirm()));

        // The administrator pins the new digest after checking it elsewhere.
        var repinned = await updater.PreviewAsync("contoso-software", null, update.Preview.Document.Sha256, CancellationToken.None);
        Assert.False(repinned.Preview.Refused);
        Assert.Contains("pinned digest changes", repinned.PinNote);
        Assert.Equal(update.Preview.Document.Sha256, updater.Apply(repinned, new UpdateConfirmation(false, false, new HashSet<string>(), new HashSet<string>(), new HashSet<string>())).PinnedDigest);
    }

    [Fact]
    public async Task AStoredDefinitionIsNotKeptUnderAnotherTrustState()
    {
        var (updater, _) = await ImportedAsync("2026-11-12T08:00:00Z", Def("app-a"), Def("app-c"));
        _body = Doc("2026-11-13T08:00:00Z", Def("app-a"));
        var (importer, _, _) = (default(VendorFeedImporter), default(VendorFeedUpdater), default(ImportedDefinitionStore));
        var update = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);
        // The new document is pinned, so its state differs from the stored "not verified" one.
        var pinned = await updater.PreviewAsync("contoso-software", null, update.Preview.Document.Sha256, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => updater.Apply(pinned, new UpdateConfirmation(false, false, new HashSet<string>(), new HashSet<string>(), new HashSet<string>())));
        _ = importer;
    }

    [Fact]
    public async Task AnIdThatAnotherFeedTookAfterThePreviewIsRefusedAtApply()
    {
        var (updater, store) = await ImportedAsync("2026-11-12T08:00:00Z", Def("app-a"));
        _body = Doc("2026-11-13T08:00:00Z", Def("app-a"), Def("app-z"));
        var update = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);
        // Another feed stores app-z in the meantime.
        var other = new FeedSource("https://other.example/feed.json", "Other", "other-feed", DateTimeOffset.UtcNow, new string('b', 64), 10, FeedTrust.NotVerifiedWording, null, null, null, [], [], []);
        var otherDefinition = FeedValidator.Validate(System.Text.Encoding.UTF8.GetBytes(Doc("2026-11-12T08:00:00Z", Def("app-z")))).Definitions.Single();
        store.Write(other, [otherDefinition]);

        Assert.Throws<InvalidOperationException>(() => updater.Apply(update, Confirm()));
        Assert.False(File.Exists(FileOf(store, "app-z")));
    }

    [Fact]
    public async Task ARenamedIdWithTheSameStageFolderIsRefusedWhileTheOldOneStays()
    {
        var (updater, store) = await ImportedAsync("2026-11-12T08:00:00Z", Def("app-a"));
        var renamed = Def("app-a2");
        renamed.Application = Def("app-a").Application;
        _body = Doc("2026-11-13T08:00:00Z", renamed);
        var update = await updater.PreviewAsync("contoso-software", null, null, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => updater.Apply(update, Confirm()));
        Assert.True(File.Exists(FileOf(store, "app-a")));
        Assert.False(File.Exists(FileOf(store, "app-a2")));

        // With the removal of the old Id confirmed, the new Id takes the folder.
        Assert.Equal(["app-a2"], updater.Apply(update, Confirm(remove: ["app-a"])).Ids);
    }

    [Fact]
    public async Task APinThatDoesNotMatchRefusesTheUpdateAndKeepsTheStoredState()
    {
        var (updater, store) = await ImportedAsync("2026-11-12T08:00:00Z", Def("app-a"));
        _body = Doc("2026-11-13T08:00:00Z", Def("app-a"), Def("app-b"));

        var update = await updater.PreviewAsync("contoso-software", null, new string('0', 64), CancellationToken.None);

        Assert.True(update.Preview.Refused);
        Assert.Throws<InvalidOperationException>(() => updater.Apply(update, Confirm()));
        Assert.False(File.Exists(FileOf(store, "app-b")));
    }
}
