using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Export and import of a site file between machines (Q-112h). Real files in a temp data root, fictional codes (AB1, CD2) and hosts under
/// example.com. The tests check that the file holds only allowlisted keys and never a planted secret, that import validates the manifest,
/// the hash of each entry, the site code rules and the entry names, that a hostile zip writes nothing, and that import creates a site or
/// merges into one without overwriting a value silently.
/// </summary>
public sealed class SiteFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-sitefile-" + Guid.NewGuid().ToString("N"));
    private readonly string _out = Path.Combine(Path.GetTempPath(), "ap-sitefile-out-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    private const string PlantedToken = "ghp_PLANTEDTOKEN0123456789";
    private const string PlantedPassword = "PlantedPassw0rd!";

    public SiteFileTests()
    {
        Directory.CreateDirectory(_out);
        SiteCatalog.Create("AB1", "cm.ab1.example.com", "Production site", _root);
        Write("AB1", """
        {
          "SiteCode": "AB1",
          "ProviderMachine": "cm.ab1.example.com",
          "DiscoverySqlServer": "sql.ab1.example.com",
          "FileShareRoot": "\\\\files.ab1.example.com\\content$",
          "GitHubToken": "ghp_PLANTEDTOKEN0123456789",
          "IntuneClientSecret": "PlantedPassw0rd!",
          "NvdApiKey": "PlantedPassw0rd!",
          "ContentDistribution": { "AutoDistribute": true, "DpGroupName": "AB1 DPs", "ApiToken": "ghp_PLANTEDTOKEN0123456789" },
          "Wsus": { "ServerName": "wsus.ab1.example.com", "UseSsl": true, "Password": "PlantedPassw0rd!" },
          "SiteHygiene": { "PauseDays": 3 },
          "OneClick": { "Destinations": { "7zip": { "ConfigMgr": true } }, "DefaultDestinations": { "ConfigMgr": true } },
          "RecentFiles": [ "C:\\secret\\thing.msi" ],
          "UserName": "someone"
        }
        """);
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _out })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Folder(string site) => SiteDataPaths.GetSiteFolder(site, dataRoot: _root);

    private void Write(string site, string json) =>
        File.WriteAllText(Path.Combine(Folder(site), "settings.json"), json, new UTF8Encoding(false));

    private string Export(string site = "AB1", string name = "site.zip")
    {
        var path = Path.Combine(_out, name);
        var result = SiteFile.Export(site, path, _root, Now);
        Assert.True(result.Ok, result.Reason);
        return path;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    /// <summary>Builds a zip from entries. The manifest lists the hash of settings.json and scope.json unless <paramref name="manifest"/> is given.</summary>
    private string Zip(IEnumerable<(string Name, byte[] Data)> entries, string name = "made.zip")
    {
        var path = Path.Combine(_out, name);
        if (File.Exists(path)) File.Delete(path);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (n, d) in entries)
        {
            using var s = zip.CreateEntry(n).Open();
            s.Write(d, 0, d.Length);
        }
        return path;
    }

    private static byte[] ManifestFor(byte[] settings, byte[] scope, string code = "AB1", string kind = SiteFile.Kind, int version = 1,
        string? settingsHash = null, long? settingsBytes = null) =>
        Bytes(new JsonObject
        {
            ["kind"] = kind,
            ["formatVersion"] = version,
            ["siteCode"] = code,
            ["label"] = "Made by test",
            ["createdUtc"] = "2026-11-23T10:00:00Z",
            ["entries"] = new JsonArray(
                new JsonObject { ["name"] = "settings.json", ["sha256"] = settingsHash ?? Hex(settings), ["bytes"] = settingsBytes ?? settings.Length },
                new JsonObject { ["name"] = "scope.json", ["sha256"] = Hex(scope), ["bytes"] = scope.Length }),
        }.ToJsonString());

    private static readonly byte[] Scope = Bytes("""{ "items": [ { "id": "apd.wsus" } ], "neverExported": [] }""");

    private string GoodZip(string settingsJson = """{ "Wsus": { "ServerName": "wsus.cd2.example.com" } }""", string code = "CD2", string name = "good.zip")
    {
        var settings = Bytes(settingsJson);
        return Zip([("manifest.json", ManifestFor(settings, Scope, code)), ("settings.json", settings), ("scope.json", Scope)], name);
    }

    private static Dictionary<string, byte[]> EntriesOf(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var ms = new MemoryStream();
            using var s = e.Open();
            s.CopyTo(ms);
            return ms.ToArray();
        });
    }

    private void AssertRefused(string path, string reasonPart)
    {
        var before = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Count();
        var read = SiteFile.Read(path);
        Assert.False(read.Ok);
        Assert.Contains(reasonPart, read.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(read.Content);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Count());
    }

    // ---- export: allowlist ----

    [Fact]
    public void ExportHoldsOnlyTheAllowlistedKeysAndNoSecret()
    {
        var path = Export();
        var entries = EntriesOf(path);
        Assert.Equal(["manifest.json", "scope.json", "settings.json"], entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var all = string.Join("\n", entries.Values.Select(v => Encoding.UTF8.GetString(v)));
        Assert.DoesNotContain(PlantedToken, all);
        Assert.DoesNotContain(PlantedPassword, all);
        Assert.DoesNotContain("GitHubToken", all);
        Assert.DoesNotContain("IntuneClientSecret", all);
        Assert.DoesNotContain("NvdApiKey", all);
        Assert.DoesNotContain("ApiToken", all);
        Assert.DoesNotContain("someone", all);
        Assert.DoesNotContain("thing.msi", all);

        var settings = JsonNode.Parse(entries["settings.json"])!.AsObject();
        var allowed = SiteSettingsImport.Items.SelectMany(i => i.Keys).Select(k => k.Split('.')[0]).ToHashSet();
        Assert.All(settings.Select(p => p.Key), k => Assert.Contains(k, allowed));
        Assert.Equal("wsus.ab1.example.com", settings["Wsus"]!["ServerName"]!.GetValue<string>());
        Assert.Null(settings["Wsus"]!["Password"]);
        Assert.Null(settings["SiteCode"]);
        Assert.Equal("AB1 DPs", settings["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
    }

    [Fact]
    public void ExportNeverCarriesTheRecordsOfTheSite()
    {
        var folder = Folder("AB1");
        Directory.CreateDirectory(Path.Combine(folder, "state"));
        File.WriteAllText(Path.Combine(folder, "state", "app-state.json"), "{\"7zip\":\"24.1\"}");
        File.WriteAllText(Path.Combine(folder, "history.db"), "PLANTED-DB");
        var path = Export();
        var all = string.Join("\n", EntriesOf(path).Values.Select(v => Encoding.UTF8.GetString(v)));
        Assert.DoesNotContain("24.1", all);
        Assert.DoesNotContain("PLANTED-DB", all);
        Assert.Equal(3, EntriesOf(path).Count);
    }

    [Fact]
    public void ExportWritesManifestWithHashOfEachEntryAndTheSiteCode()
    {
        var entries = EntriesOf(Export());
        var manifest = JsonNode.Parse(entries["manifest.json"])!.AsObject();
        Assert.Equal(SiteFile.Kind, manifest["kind"]!.GetValue<string>());
        Assert.Equal("AB1", manifest["siteCode"]!.GetValue<string>());
        Assert.Equal("Production site", manifest["label"]!.GetValue<string>());
        foreach (var e in manifest["entries"]!.AsArray())
            Assert.Equal(Hex(entries[e!["name"]!.GetValue<string>()]), e["sha256"]!.GetValue<string>());
    }

    [Fact]
    public void ExportOfASiteThatDoesNotExistFailsAndWritesNothing()
    {
        var path = Path.Combine(_out, "none.zip");
        var result = SiteFile.Export("ZZ9", path, _root, Now);
        Assert.False(result.Ok);
        Assert.False(File.Exists(path));
        Assert.False(SiteFile.Export("../AB1", path, _root, Now).Ok);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ExportLeavesNoTemporaryFileAndReplacesAnOldFile()
    {
        var path = Path.Combine(_out, "again.zip");
        File.WriteAllText(path, "old");
        Assert.True(SiteFile.Export("AB1", path, _root, Now).Ok);
        Assert.True(SiteFile.Read(path).Ok);
        Assert.Empty(Directory.GetFiles(_out, "*.exporting"));
    }

    [Fact]
    public void ExportDoesNotChangeTheSite()
    {
        var before = Directory.EnumerateFiles(Folder("AB1"), "*", SearchOption.AllDirectories).ToDictionary(f => f, f => Hex(File.ReadAllBytes(f)));
        Export();
        var after = Directory.EnumerateFiles(Folder("AB1"), "*", SearchOption.AllDirectories).ToDictionary(f => f, f => Hex(File.ReadAllBytes(f)));
        Assert.Equal(before, after);
    }

    // ---- import: valid file ----

    [Fact]
    public void ARoundTripCreatesANewSiteWithTheSameSettings()
    {
        var path = Export();
        var read = SiteFile.Read(path);
        Assert.True(read.Ok, read.Reason);
        var content = read.Content!;
        Assert.Equal("AB1", content.SiteCode);
        // AB1 is in use here: a new folder is a different environment.
        Assert.Equal("AB1~2", SiteFile.NewFolderNameFor(content, _root));

        var request = SiteFile.ImportRequest(content, "AB1~2", null, null, _root, () => Now);
        var plan = SiteSettingsImport.Plan(request);
        Assert.Equal(SiteImportReport.Done, plan.Outcome);
        Assert.False(Directory.Exists(Folder("AB1~2")));

        var run = SiteSettingsImport.Run(request);
        Assert.Equal(SiteImportReport.Done, run.Outcome);
        Assert.True(Directory.Exists(Folder("AB1~2")));
        var created = SiteCatalog.Get("AB1~2", _root)!;
        Assert.Equal("AB1", created.SiteCode);
        Assert.Equal("Production site", created.Label);
        var settings = SiteSettingsSplit.ReadObject(Path.Combine(Folder("AB1~2"), "settings.json"))!;
        Assert.Equal("AB1 DPs", settings["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        // Environment values are off by default.
        Assert.Null(settings["Wsus"]);
        Assert.Null(settings["ProviderMachine"]);
        var log = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder("AB1~2"), SiteSettingsImport.LogFileName)))!;
        Assert.Equal("file", log["sourceKind"]!.GetValue<string>());
        Assert.DoesNotContain(PlantedToken, File.ReadAllText(Path.Combine(Folder("AB1~2"), "settings.json")));
    }

    [Fact]
    public void ImportIntoANewSiteWithAllItemsTickedCopiesTheEnvironmentValuesToo()
    {
        var content = SiteFile.Read(Export())!.Content!;
        var ids = SiteSettingsImport.Items.Select(i => i.Id).ToList();
        var run = SiteSettingsImport.Run(SiteFile.ImportRequest(content, "AB1~2", ids, null, _root, () => Now));
        Assert.Equal(SiteImportReport.Done, run.Outcome);
        var settings = SiteSettingsSplit.ReadObject(Path.Combine(Folder("AB1~2"), "settings.json"))!;
        Assert.Equal("wsus.ab1.example.com", settings["Wsus"]!["ServerName"]!.GetValue<string>());
        Assert.Contains("cm.ab1.example.com", SiteCatalog.Get("AB1~2", _root)!.Providers);
    }

    [Fact]
    public void ImportIntoAnExistingSiteKeepsAnExistingValueByDefaultAndReplacesOnlyOnChoice()
    {
        SiteCatalog.Create("CD2", "cm.cd2.example.com", "Other", _root);
        Write("CD2", """{ "ContentDistribution": { "DpGroupName": "CD2 own group" }, "SiteHygiene": { "PauseDays": 9 } }""");
        var content = SiteFile.Read(Export())!.Content!;
        var target = Path.Combine(Folder("CD2"), "settings.json");
        var before = Hex(File.ReadAllBytes(target));

        var keep = SiteSettingsImport.Run(SiteFile.ImportRequest(content, "CD2", null, null, _root, () => Now));
        var afterKeep = SiteSettingsSplit.ReadObject(target)!;
        Assert.Equal("CD2 own group", afterKeep["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        Assert.Equal(9, afterKeep["SiteHygiene"]!["PauseDays"]!.GetValue<int>());
        Assert.True(keep.Count(SiteImportOutcome.Kept) >= 2);
        // Items that the target did not have are added; nothing existing is replaced.
        Assert.NotNull(afterKeep["DistributionOverrides"] ?? afterKeep["OneClick"]);
        Assert.Empty(Directory.GetFiles(Folder("CD2"), "settings.json.before-import-*"));

        var replace = SiteSettingsImport.Run(SiteFile.ImportRequest(content, "CD2", null,
            new Dictionary<string, SiteImportChoice> { ["apd.content-distribution"] = SiteImportChoice.Replace }, _root, () => Now.AddMinutes(1)));
        var afterReplace = SiteSettingsSplit.ReadObject(target)!;
        Assert.Equal("AB1 DPs", afterReplace["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        Assert.Equal(9, afterReplace["SiteHygiene"]!["PauseDays"]!.GetValue<int>());
        Assert.Equal(1, replace.Count(SiteImportOutcome.Replaced));
        Assert.Single(Directory.GetFiles(Folder("CD2"), "settings.json.before-import-*"));
        Assert.NotEqual(before, Hex(File.ReadAllBytes(target)));
    }

    [Fact]
    public void ImportOfTheSameFileTwiceChangesNothingTheSecondTime()
    {
        var content = SiteFile.Read(Export())!.Content!;
        var request = SiteFile.ImportRequest(content, "AB1~2", null, null, _root, () => Now);
        Assert.Equal(SiteImportReport.Done, SiteSettingsImport.Run(request).Outcome);
        var target = Path.Combine(Folder("AB1~2"), "settings.json");
        var hash = Hex(File.ReadAllBytes(target));
        var second = SiteSettingsImport.Run(request);
        Assert.Equal(SiteImportReport.NothingToDo, second.Outcome);
        Assert.Equal(hash, Hex(File.ReadAllBytes(target)));
    }

    [Fact]
    public void AFileOfTheSameSiteCodeCanMergeIntoThatSiteOnAnotherMachine()
    {
        // The file of AB1 into AB1 itself: allowed for a file (the other machine has its own AB1), refused for a site import.
        var content = SiteFile.Read(Export())!.Content!;
        var run = SiteSettingsImport.Plan(SiteFile.ImportRequest(content, "AB1", null, null, _root, () => Now));
        Assert.NotEqual(SiteImportReport.Refused, run.Outcome);
        Assert.Equal(SiteImportReport.NothingToDo, run.Outcome);
    }

    [Fact]
    public void ImportPlanWritesNothing()
    {
        var content = SiteFile.Read(Export())!.Content!;
        var before = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Count();
        SiteSettingsImport.Plan(SiteFile.ImportRequest(content, "AB1~2", null, null, _root, () => Now));
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void ANewSiteNameFollowsTheNameRules()
    {
        var content = SiteFile.Read(GoodZip(code: "CD2"))!.Content!;
        Assert.Equal("CD2", SiteFile.NewFolderNameFor(content, _root));
        var request = SiteFile.ImportRequest(content, "../CD2", null, null, _root, () => Now);
        Assert.Equal(SiteImportReport.Refused, SiteSettingsImport.Run(request).Outcome);
        Assert.False(Directory.Exists(Path.Combine(_root, "CD2")));
    }

    // ---- import: refused files ----

    [Fact]
    public void AFileThatIsNotAZipIsRefused()
    {
        var path = Path.Combine(_out, "text.zip");
        File.WriteAllText(path, "this is not a zip");
        AssertRefused(path, "not a valid zip");
        var empty = Path.Combine(_out, "empty.zip");
        File.WriteAllBytes(empty, []);
        AssertRefused(empty, "empty");
        AssertRefused(Path.Combine(_out, "missing.zip"), "does not exist");
    }

    [Fact]
    public void AMissingEntryIsRefused()
    {
        var settings = Bytes("{}");
        AssertRefused(Zip([("settings.json", settings), ("scope.json", Scope)]), "manifest.json' is missing");
        AssertRefused(Zip([("manifest.json", ManifestFor(settings, Scope)), ("settings.json", settings)]), "scope.json' is missing");
    }

    [Fact]
    public void ABadManifestIsRefused()
    {
        var settings = Bytes("""{ "Wsus": { "UseSsl": true } }""");
        AssertRefused(Zip([("manifest.json", ManifestFor(settings, Scope, kind: "something-else")), ("settings.json", settings), ("scope.json", Scope)]), "not the manifest of a site file");
        AssertRefused(Zip([("manifest.json", ManifestFor(settings, Scope, version: 2)), ("settings.json", settings), ("scope.json", Scope)]), "format version 2");
        AssertRefused(Zip([("manifest.json", ManifestFor(settings, Scope, version: 0)), ("settings.json", settings), ("scope.json", Scope)]), "not valid");
        AssertRefused(Zip([("manifest.json", Bytes("[1,2]")), ("settings.json", settings), ("scope.json", Scope)]), "not a JSON object");
        AssertRefused(Zip([("manifest.json", Bytes("{ nope")), ("settings.json", settings), ("scope.json", Scope)]), "not a JSON object");
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("ABCD")]
    [InlineData("ab1")]
    [InlineData("CON")]
    [InlineData("SMS")]
    [InlineData("NUL")]
    [InlineData("../")]
    [InlineData("A..")]
    [InlineData("A/B")]
    [InlineData("A\\B")]
    [InlineData("AB1~2")]
    [InlineData("")]
    public void ASiteCodeThatBreaksTheNameRulesIsRefused(string code)
    {
        var settings = Bytes("{}");
        var path = Zip([("manifest.json", ManifestFor(settings, Scope, code)), ("settings.json", settings), ("scope.json", Scope)]);
        var read = SiteFile.Read(path);
        Assert.False(read.Ok);
        Assert.Contains("site code", read.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATamperedEntryFailsTheHashCheck()
    {
        var good = Bytes("""{ "Wsus": { "ServerName": "wsus.cd2.example.com" } }""");
        var tampered = Bytes("""{ "Wsus": { "ServerName": "evil.example.com" } }""");
        var path = Zip([("manifest.json", ManifestFor(good, Scope)), ("settings.json", tampered), ("scope.json", Scope)]);
        AssertRefused(path, "SHA-256 of settings.json does not match");
    }

    [Fact]
    public void AWrongSizeInTheManifestIsRefused()
    {
        var settings = Bytes("{}");
        AssertRefused(Zip([("manifest.json", ManifestFor(settings, Scope, settingsBytes: 5)), ("settings.json", settings), ("scope.json", Scope)]), "size of settings.json");
        AssertRefused(Zip([("manifest.json", ManifestFor(settings, Scope, settingsHash: "xyz")), ("settings.json", settings), ("scope.json", Scope)]), "not a SHA-256");
    }

    [Fact]
    public void ASecretNameInTheSettingsIsRefusedEvenWithAMatchingHash()
    {
        var withSecret = Bytes("""{ "Wsus": { "ServerName": "wsus.cd2.example.com", "Password": "x" } }""");
        AssertRefused(Zip([("manifest.json", ManifestFor(withSecret, Scope)), ("settings.json", withSecret), ("scope.json", Scope)]), "secret-like name");
        var topLevel = Bytes("""{ "GitHubToken": "ghp_x" }""");
        AssertRefused(Zip([("manifest.json", ManifestFor(topLevel, Scope)), ("settings.json", topLevel), ("scope.json", Scope)]), "does not carry");
    }

    [Fact]
    public void ASettingOutsideTheAllowlistIsRefusedEvenWithAMatchingHash()
    {
        foreach (var json in new[] { """{ "SiteCode": "AB1" }""", """{ "UserName": "x" }""", """{ "Wsus": { "ServerName": "a" }, "RecentFiles": [] }""", """{ "OneClick": { "Other": 1 } }""" })
        {
            var bytes = Bytes(json);
            AssertRefused(Zip([("manifest.json", ManifestFor(bytes, Scope)), ("settings.json", bytes), ("scope.json", Scope)]), "does not carry");
        }
    }

    [Fact]
    public void AnUnknownItemInTheScopeIsRefused()
    {
        var settings = Bytes("{}");
        var scope = Bytes("""{ "items": [ { "id": "apd.github-token" } ] }""");
        AssertRefused(Zip([("manifest.json", ManifestFor(settings, scope)), ("settings.json", settings), ("scope.json", scope)]), "item that this version does not know");
    }

    // ---- hostile zips ----

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("..")]
    [InlineData("/etc/evil")]
    [InlineData("\\\\server\\share\\evil")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("C:evil")]
    [InlineData("sub/settings.json")]
    [InlineData("settings.json/../../evil")]
    [InlineData("sub\\scope.json")]
    [InlineData("~/evil")]
    [InlineData("evil.txt")]
    [InlineData("Settings.json")]
    [InlineData("settings.json ")]
    [InlineData("settings.json:stream")]
    [InlineData("manifest.json/")]
    public void AHostileEntryNameIsRefusedAndNothingIsWritten(string hostile)
    {
        var settings = Bytes("{}");
        var path = Zip([("manifest.json", ManifestFor(settings, Scope)), ("settings.json", settings), ("scope.json", Scope), (hostile, Bytes("MZ-evil"))]);
        var before = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Count();
        var parent = Directory.GetParent(_out)!.FullName;
        var read = SiteFile.Read(path);
        Assert.False(read.Ok, hostile);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Count());
        Assert.False(File.Exists(Path.Combine(parent, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_out, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void AHostileNameInPlaceOfARequiredEntryIsRefused()
    {
        var settings = Bytes("{}");
        var path = Zip([("manifest.json", ManifestFor(settings, Scope)), ("../settings.json", settings), ("scope.json", Scope)]);
        AssertRefused(path, "not a plain file name");
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(_out)!.FullName, "settings.json")));
    }

    [Fact]
    public void ADuplicateEntryIsRefused()
    {
        var settings = Bytes("{}");
        var path = Zip([("manifest.json", ManifestFor(settings, Scope)), ("settings.json", settings), ("scope.json", Scope), ("settings.json", Bytes("{\"Wsus\":{\"UseSsl\":true}}"))]);
        var read = SiteFile.Read(path);
        Assert.False(read.Ok);
    }

    [Fact]
    public void AnEntryThatUnpacksTooLargeIsRefused()
    {
        var settings = Bytes("{}");
        var bomb = new byte[SiteFile.MaxEntryBytes + 10_000];
        Array.Fill(bomb, (byte)' ');
        var path = Zip([("manifest.json", ManifestFor(settings, Scope)), ("settings.json", bomb), ("scope.json", Scope)]);
        Assert.True(new FileInfo(path).Length < SiteFile.MaxFileBytes);
        AssertRefused(path, "larger than");
    }

    [Fact]
    public void AFileThatIsTooLargeIsRefusedBeforeItIsOpened()
    {
        var path = Path.Combine(_out, "big.zip");
        File.WriteAllBytes(path, new byte[SiteFile.MaxFileBytes + 1]);
        AssertRefused(path, "larger than");
    }

    [Fact]
    public void AnEntryWithADeclaredSizeThatLiesIsStillLimited()
    {
        // Patch the uncompressed size in the central directory to a small number: the bounded read still stops at the limit.
        var settings = Bytes("{}");
        var bomb = new byte[SiteFile.MaxEntryBytes + 10_000];
        Array.Fill(bomb, (byte)' ');
        var path = Zip([("manifest.json", ManifestFor(settings, Scope)), ("settings.json", bomb), ("scope.json", Scope)]);
        var data = File.ReadAllBytes(path);
        // Central directory header signature 0x02014B50; uncompressed size is at offset 24 of the header.
        for (var i = 0; i < data.Length - 28; i++)
        {
            if (data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x01 && data[i + 3] == 0x02)
            {
                var size = BitConverter.ToUInt32(data, i + 24);
                if (size == bomb.Length) BitConverter.GetBytes(10u).CopyTo(data, i + 24);
            }
        }
        File.WriteAllBytes(path, data);
        var read = SiteFile.Read(path);
        Assert.False(read.Ok);
    }

    [Fact]
    public void ATruncatedZipIsRefused()
    {
        var path = Export();
        var data = File.ReadAllBytes(path);
        var cut = Path.Combine(_out, "cut.zip");
        File.WriteAllBytes(cut, data[..(data.Length / 2)]);
        Assert.False(SiteFile.Read(cut).Ok);
    }

    [Fact]
    public void ALongOrControlLabelIsCleaned()
    {
        var settings = Bytes("{}");
        var manifest = JsonNode.Parse(ManifestFor(settings, Scope))!.AsObject();
        manifest["label"] = "Line\u0001one" + new string('x', 200);
        var path = Zip([("manifest.json", Bytes(manifest.ToJsonString())), ("settings.json", settings), ("scope.json", Scope)]);
        var read = SiteFile.Read(path);
        Assert.True(read.Ok, read.Reason);
        Assert.DoesNotContain('\u0001', read.Content!.Label);
        Assert.True(read.Content.Label.Length <= SiteFile.MaxLabelLength);
    }

    [Fact]
    public void AFileMadeByThePowerShellAppIsReadAndImported()
    {
        // Written by Export-AppSiteFile of the PowerShell app (fictional site AB1): the two apps share one format.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SiteFile", "powershell-made-ab1.zip");
        var read = SiteFile.Read(path);
        Assert.True(read.Ok, read.Reason);
        Assert.Equal("AB1", read.Content!.SiteCode);
        Assert.Equal("wsus.ab1.example.com", read.Content.Settings["Wsus"]!["ServerName"]!.GetValue<string>());
        Assert.Equal("cm.ab1.example.com", read.Content.Settings["ProviderMachine"]!.GetValue<string>());
        var run = SiteSettingsImport.Run(SiteFile.ImportRequest(read.Content, SiteFile.NewFolderNameFor(read.Content, _root), SiteSettingsImport.Items.Select(i => i.Id).ToList(), null, _root, () => Now));
        Assert.Equal(SiteImportReport.Done, run.Outcome);
    }

    [Fact]
    public void TheSuggestedFileNameHasNoPathCharacters()
    {
        var name = SiteFile.SuggestedFileName("AB1~2", Now);
        Assert.Equal("site-AB1-2-20261123.zip", name);
        Assert.Equal(name, Path.GetFileName(name));
    }
}
