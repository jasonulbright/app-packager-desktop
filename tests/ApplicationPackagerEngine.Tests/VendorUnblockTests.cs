using System.Net;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The unblock of a script or a command (step 3 of the vendor import): which code can be unblocked, what the digest covers, and what is stored.</summary>
public sealed class VendorUnblockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-unblock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AppDefinition Base(string id = "contoso-agent") => new()
    {
        Id = id, Vendor = "Contoso", Application = "Contoso " + id, CMName = "Contoso Agent", Architecture = "x64",
        VendorUrl = "https://contoso.example/", InstallerType = "MSI", DownloadUrl = "https://contoso.example/agent-{version}.msi",
        InstallArgs = "/qn", Detection = new DetectionConfig { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\Contoso" }
    };

    private static string Json(AppDefinition d) => JsonSerializer.Serialize(d);

    private static string Doc(params AppDefinition[] definitions) =>
        "{\"FeedFormat\":1,\"Vendor\":\"Contoso Software\",\"FeedId\":\"contoso-software\",\"Updated\":\"2026-11-12T08:00:00Z\",\"Definitions\":["
        + string.Join(",", definitions.Select(Json)) + "]}";

    private VendorFeedImporter Importer(params AppDefinition[] definitions) =>
        new(new FeedFetcher(() => new FixedHandler(Doc(definitions)), null, (_, _) => Task.CompletedTask), new ImportedDefinitionStore(_root),
            () => new HashSet<string>(StringComparer.OrdinalIgnoreCase), () => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new FixedClock(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)));

    private static Task<ImportPreview> PreviewAsync(VendorFeedImporter importer) =>
        importer.PreviewAsync(new Uri("https://vendor.example/feed.json"), null, CancellationToken.None);

    private static ImportConfirmation Confirm(params string[] unblock) =>
        new("Contoso Software", "contoso-software", true, new HashSet<string>(), new HashSet<string>(unblock, StringComparer.OrdinalIgnoreCase));

    private static AppDefinition WithCommand(string id = "contoso-agent")
    {
        var d = Base(id);
        d.UninstallCommand = "msiexec /x {GUID} /qn";
        return d;
    }

    private static AppDefinition WithLocalFile(string id)
    {
        var d = WithCommand(id);
        d.LocalInstaller = new LocalInstallerConfig();
        return d;
    }

    [Fact]
    public void TheAnalysisNamesTheSamePiecesOfCodeAsTheCheck()
    {
        var d = Base();
        d.UninstallCommand = "msiexec /x {GUID}";
        d.PostInstallScript = new PostInstallScriptConfig { Script = "Write-Host done" };
        d.ExpandInstallArgs = true;
        d.InstallerSettings = [new InstallerParameter { Key = "Path", CommandFragment = "/D={value}", UninstallFragment = "/R" }];
        d.Detection = new DetectionConfig { Type = "Script", ScriptText = "exit 0" };
        d.LocalInstaller = new LocalInstallerConfig();

        Assert.Equal(DefinitionCode.Find(d), DefinitionUnblock.Analyze(d).Select(i => i.Name));
    }

    [Fact]
    public void ScriptsAndCommandsCanBeUnblockedButAPathToALocalFileCannot()
    {
        Assert.True(DefinitionUnblock.CanUnblock(WithCommand()));
        Assert.False(DefinitionUnblock.CanUnblock(WithLocalFile("contoso-local")));
        Assert.False(DefinitionUnblock.CanUnblock(Base()));
        var bootstrapper = Base();
        bootstrapper.BootstrapperLayout = new BootstrapperLayoutConfig();
        Assert.False(DefinitionUnblock.CanUnblock(bootstrapper));
    }

    [Fact]
    public void TheDigestChangesWithTheTextOfACommandAndWithEverySettingItUses()
    {
        static AppDefinition Make(string command, string defaultValue, string? option = null)
        {
            var d = WithCommand();
            d.UninstallCommand = command;
            d.ExpandInstallArgs = true;
            d.InstallArgs = "/qn INSTALLDIR={setting:Dir}";
            d.InstallerSettings = [new InstallerParameter { Key = "Dir", ControlType = "Text", DefaultValue = defaultValue, Options = option is null ? null : [option], ValueMap = new() { ["a"] = "b" } }];
            return d;
        }

        var baseline = DefinitionUnblock.Digest(Make("msiexec /x {GUID}", "C:\\A"));

        Assert.Equal(baseline, DefinitionUnblock.Digest(Make("msiexec /x {GUID}", "C:\\A")));
        Assert.NotEqual(baseline, DefinitionUnblock.Digest(Make("msiexec /x {GUID} /norestart", "C:\\A")));
        Assert.NotEqual(baseline, DefinitionUnblock.Digest(Make("msiexec /x {GUID}", "C:\\B")));
        Assert.NotEqual(baseline, DefinitionUnblock.Digest(Make("msiexec /x {GUID}", "C:\\A", option: "x")));
    }

    [Fact]
    public async Task ADefinitionWithOnlyACommandOffersTheUnblockAndOneWithALocalFilePathDoesNot()
    {
        var preview = await PreviewAsync(Importer(WithCommand(), WithLocalFile("contoso-local"), Base("contoso-plain")));

        Assert.True(preview.Candidates.Single(c => c.Definition.Id == "contoso-agent").CanUnblock);
        Assert.False(preview.Candidates.Single(c => c.Definition.Id == "contoso-local").CanUnblock);
        Assert.False(preview.Candidates.Single(c => c.Definition.Id == "contoso-plain").CanUnblock);
    }

    [Fact]
    public async Task AnUnblockedDefinitionIsStoredWithTheDigestOfItsCodeAndASkippedOneIsNot()
    {
        var importer = Importer(WithCommand(), Base("contoso-plain"));
        var preview = await PreviewAsync(importer);

        var source = importer.Import(preview, Confirm("contoso-agent"));

        Assert.Equal(["contoso-agent", "contoso-plain"], source.Ids.Order());
        Assert.Equal(["contoso-agent:" + DefinitionUnblock.Digest(WithCommand())], source.UnblockedDigests);
    }

    [Fact]
    public async Task WithoutTheUnblockTheDefinitionThatCarriesACommandStaysOut()
    {
        var importer = Importer(WithCommand(), Base("contoso-plain"));
        var preview = await PreviewAsync(importer);

        var source = importer.Import(preview, Confirm());

        Assert.Equal(["contoso-plain"], source.Ids);
        Assert.Empty(source.UnblockedDigests);
    }

    [Fact]
    public async Task AnUnblockOfALocalFilePathOrOfAnUnknownIdIsRefusedAndNothingIsWritten()
    {
        var importer = Importer(WithLocalFile("contoso-local"), Base("contoso-plain"));
        var preview = await PreviewAsync(importer);

        Assert.Throws<InvalidOperationException>(() => importer.Import(preview, Confirm("contoso-local")));
        Assert.Throws<InvalidOperationException>(() => importer.Import(preview, Confirm("nothing-here")));
        Assert.False(Directory.Exists(_root));
    }


    [Fact]
    public async Task AnExtractSetupAndAControlCharacterInACommandAreNeverUnblocked()
    {
        var extract = WithCommand("contoso-extract");
        extract.ExtractSetup = new ExtractSetupConfig { ExtractDirName = "Contoso" };
        var control = Base("contoso-control");
        control.UninstallCommand = "msiexec /x {GUID}\u0000 && calc";
        var preview = await PreviewAsync(Importer(extract, control));

        Assert.False(preview.Candidates.Single(c => c.Definition.Id == "contoso-extract").CanUnblock);
        Assert.False(preview.Candidates.Single(c => c.Definition.Id == "contoso-control").CanUnblock);
    }

    [Fact]
    public void TheDigestCoversTheLanguageAndTheBitnessOfADetectionScript()
    {
        AppDefinition Make(string language, bool is64)
        {
            var d = Base();
            d.Detection = new DetectionConfig { Type = "Script", ScriptText = "exit 0", ScriptLanguage = language, Is64Bit = is64 };
            return d;
        }

        var baseline = DefinitionUnblock.Digest(Make("PowerShell", true));

        Assert.NotEqual(baseline, DefinitionUnblock.Digest(Make("VBScript", true)));
        Assert.NotEqual(baseline, DefinitionUnblock.Digest(Make("PowerShell", false)));
    }

    [Fact]
    public async Task AClearDefinitionKeepsItsStageFolderWhenAnUnblockableOneWithTheSameFolderIsNotUnblocked()
    {
        var blocked = WithCommand("contoso-blocked");
        var clear = Base("contoso-clear");
        clear.Application = blocked.Application;
        var importer = Importer(blocked, clear);
        var preview = await PreviewAsync(importer);

        Assert.Equal(ConflictKind.None, preview.Candidates.Single(c => c.Definition.Id == "contoso-clear").Conflict);
        Assert.Equal(["contoso-clear"], importer.Import(preview, Confirm()).Ids);
    }

    [Fact]
    public async Task UnblockingADefinitionWithTheStageFolderOfAnotherTakenDefinitionIsRefused()
    {
        var blocked = WithCommand("contoso-blocked");
        var clear = Base("contoso-clear");
        clear.Application = blocked.Application;
        var importer = Importer(blocked, clear);
        var preview = await PreviewAsync(importer);

        Assert.Throws<InvalidOperationException>(() => importer.Import(preview, Confirm("contoso-blocked")));
        Assert.False(Directory.Exists(_root));
    }
    [Fact]
    public void TheUnblockWindowShowsTheFullTextOfEveryCommandWithItsSha256AndTheDigest()
    {
        var text = DefinitionUnblock.Describe(WithCommand());

        Assert.Contains("msiexec /x {GUID} /qn", text);
        Assert.Contains("SHA-256 " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("msiexec /x {GUID} /qn"))), text);
        Assert.Contains(DefinitionUnblock.Digest(WithCommand()), text);
    }
}
