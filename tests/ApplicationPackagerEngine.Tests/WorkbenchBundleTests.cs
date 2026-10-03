using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Portable bundles: export writes a SHA-256 per entry; import verifies every hash,
/// refuses unlisted, escaping, stream-named and linked entries, gates on the schema
/// version and replaces an existing application only when asked to.
/// </summary>
public sealed class WorkbenchBundleTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly WorkbenchStore _second;

    public WorkbenchBundleTests() => _second = new WorkbenchStore(Path.Combine(_t.Root, "second-data"));

    private WorkbenchStore Store => _t.Store;

    public void Dispose()
    {
        // Junctions are removed first so the recursive delete never walks into a target.
        foreach (var link in Directory.GetDirectories(_t.Root, "*", SearchOption.AllDirectories)
                     .Where(WorkbenchJson.IsReparsePoint).OrderByDescending(p => p.Length).ToList())
            try { Directory.Delete(link); } catch (IOException) { }
        _t.Dispose();
    }

    private string BundlePath(string name = "bundle.zip") => Path.Combine(_t.NewFolder(), name);

    private string NewByo(string content = "binary-v1")
    {
        var installer = _t.NewFile(Path.Combine(_t.NewFolder(), "tool.exe"), content);
        return Store.SaveByoApplication(installer, new ByoInstallerIdentity("EXE", "x64", null, "Tool", "Contoso", "1.0"),
            "Tool", "Contoso", "1.0", new ByoStageValues { InstallArgs = "/S", Confirmed = true }).ApplicationId;
    }

    /// <summary>Writes a zip with the given entries and a bundle.json listing <paramref name="listed"/> with their real hashes.</summary>
    private string HostileBundle(IReadOnlyDictionary<string, string> entries, IEnumerable<string>? listed = null,
        Func<string, string>? hashOverride = null, int schema = 1, string applicationId = "catalog:package-hostile")
    {
        var path = BundlePath("hostile.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifestEntries = new JsonArray();
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using (var stream = entry.Open()) stream.Write(Encoding.UTF8.GetBytes(content));
        }
        foreach (var relative in listed ?? entries.Keys.Where(k => k.StartsWith("application/")).Select(k => k["application/".Length..].Replace('/', '\\')))
        {
            var content = entries.TryGetValue("application/" + relative.Replace('\\', '/'), out var c) ? c : "";
            var hash = hashOverride?.Invoke(relative) ?? WorkbenchJson.TextSha256(content);
            manifestEntries.Add(new JsonObject { ["RelativePath"] = relative, ["Sha256"] = hash, ["Size"] = content.Length });
        }
        var manifest = new JsonObject
        {
            ["SchemaVersion"] = schema, ["ApplicationId"] = applicationId, ["SourcesIncluded"] = true,
            ["SourceStatus"] = "Complete", ["Entries"] = manifestEntries
        };
        var manifestEntry = zip.CreateEntry("bundle.json");
        using (var stream = manifestEntry.Open()) stream.Write(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        return path;
    }

    private static Dictionary<string, string> Payload(params (string Name, string Content)[] extra)
    {
        var entries = new Dictionary<string, string>
        {
            ["application/application.json"] = """{"SchemaVersion":1,"ApplicationId":"catalog:package-hostile","DisplayName":"Hostile"}"""
        };
        foreach (var (name, content) in extra) entries[name] = content;
        return entries;
    }

    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(30_000);
        Assert.True(WorkbenchJson.IsReparsePoint(link), "the junction was not created");
    }

    private bool AnyApplication(WorkbenchStore store) =>
        Directory.Exists(store.ApplicationsRoot) && Directory.EnumerateFileSystemEntries(store.ApplicationsRoot).Any();

    // ── Round trip ─────────────────────────────────────────────

    [Fact]
    public void ExportThenImportUnderANewId_RecreatesTheApplication()
    {
        const string id = "catalog:package-bundle";
        var profile = _t.NewProfile(id);
        Store.SaveApplicationDefinition(new WorkbenchApplicationDefinition { ApplicationId = id, DisplayName = "Bundled", ActiveProfileId = profile.ProfileId });

        var bundle = BundlePath();
        var exported = WorkbenchBundles.Export(Store, id, bundle);
        Assert.True(File.Exists(bundle));
        Assert.Equal(2, exported.EntryCount);

        const string targetId = "catalog:package-bundle-copy";
        var imported = WorkbenchBundles.Import(Store, bundle, targetId);
        Assert.Equal(targetId, imported.ApplicationId);
        var definition = Store.GetApplicationDefinition(targetId);
        Assert.True(definition.Persisted);
        Assert.Equal(targetId, definition.ApplicationId);
        Assert.Equal("Bundled", definition.DisplayName);
        var copied = Store.GetProfile(targetId, profile.ProfileId);
        Assert.Equal(targetId, copied.ApplicationId);
        Assert.Equal(profile.Revision, copied.Revision);
    }

    [Fact]
    public void TheManifestListsEveryEntryWithItsRelativePathAndSha256()
    {
        var id = NewByo();
        var bundle = BundlePath();
        WorkbenchBundles.Export(Store, id, bundle, includeSources: true);

        var manifest = WorkbenchBundles.ReadManifest(bundle);
        Assert.Equal(WorkbenchBundles.SchemaVersion, manifest.SchemaVersion);
        Assert.Equal(id, manifest.ApplicationId);
        Assert.True(manifest.SourcesIncluded);
        Assert.Equal("Complete", manifest.SourceStatus);
        var folder = Store.GetApplicationFolder(id);
        Assert.Equal(["application.json", @"sources\1\tool.exe"], manifest.Entries.Select(e => e.RelativePath).Order());
        foreach (var entry in manifest.Entries)
            Assert.Equal(WorkbenchJson.FileSha256(Path.Combine(folder, entry.RelativePath)), entry.Sha256);
        using var zip = ZipFile.OpenRead(bundle);
        Assert.Contains(zip.Entries, e => e.FullName == "application/sources/1/tool.exe");
    }

    [Fact]
    public void WithoutSources_TheInstallerStaysOut_AndTheBundleSaysSo()
    {
        var id = NewByo();
        var bundle = BundlePath();
        var exported = WorkbenchBundles.Export(Store, id, bundle);
        Assert.False(exported.SourcesIncluded);

        var manifest = WorkbenchBundles.ReadManifest(bundle);
        Assert.False(manifest.SourcesIncluded);
        Assert.Equal("IncompleteSource", manifest.SourceStatus);
        Assert.DoesNotContain(manifest.Entries, e => e.RelativePath.StartsWith("sources", StringComparison.OrdinalIgnoreCase));
        using (var zip = ZipFile.OpenRead(bundle))
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("sources/", StringComparison.OrdinalIgnoreCase));

        var imported = WorkbenchBundles.Import(_second, bundle);
        Assert.Equal("IncompleteSource", imported.SourceStatus);
        Assert.Equal(id, imported.ApplicationId);
        var error = Assert.Throws<FileNotFoundException>(() => _second.ResolveByoSource(id));
        Assert.Contains("is missing its installer", error.Message);
    }

    [Fact]
    public void WithSources_TheImportedByoApplicationResolvesItsInstallerUnderTheNewDataRoot()
    {
        var id = NewByo();
        var bundle = BundlePath();
        WorkbenchBundles.Export(Store, id, bundle, includeSources: true);

        var imported = WorkbenchBundles.Import(_second, bundle);
        Assert.Equal("Complete", imported.SourceStatus);
        var resolved = _second.ResolveByoSource(id);
        Assert.StartsWith(_second.DataRoot, resolved.InstallerPath);
        Assert.Equal("binary-v1", File.ReadAllText(resolved.InstallerPath));
        Assert.Equal(resolved.InstallerPath, resolved.Source.Path);
        Assert.Equal("/S", resolved.Source.StageValues!.InstallArgs);
        Assert.Single(_second.DiscoverApplications(), a => a.ApplicationId == id && a.Origin == ApplicationOrigin.Byo);
    }

    [Fact]
    public void AnApplicationWithNothingStored_HasNothingToExport()
    {
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Export(Store, "catalog:package-never", BundlePath()));
        Assert.Contains("has nothing stored to export", error.Message);
    }

    // ── Existing application ───────────────────────────────────

    [Fact]
    public void ImportingOverAnExistingApplicationWithoutForce_IsRefused()
    {
        const string id = "catalog:package-bundle-existing";
        Store.SaveApplicationDefinition(new WorkbenchApplicationDefinition { ApplicationId = id, DisplayName = "Existing" });
        var bundle = BundlePath();
        WorkbenchBundles.Export(Store, id, bundle);

        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.Contains("already exists", error.Message);
    }

    [Fact]
    public void ImportingWithForce_ReplacesTheExistingApplication()
    {
        const string id = "catalog:package-bundle-replace";
        Store.SaveApplicationDefinition(new WorkbenchApplicationDefinition { ApplicationId = id, DisplayName = "From bundle" });
        var bundle = BundlePath();
        WorkbenchBundles.Export(Store, id, bundle);
        Store.SaveApplicationDefinition(new WorkbenchApplicationDefinition { ApplicationId = id, DisplayName = "Edited since" });
        var stray = _t.NewFile(Path.Combine(Store.GetApplicationFolder(id), "stray.txt"), "left over");

        WorkbenchBundles.Import(Store, bundle, force: true);

        Assert.Equal("From bundle", Store.GetApplicationDefinition(id).DisplayName);
        Assert.False(File.Exists(stray));
        Assert.DoesNotContain(Directory.GetDirectories(Store.ApplicationsRoot), d => Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public void AFailedCheck_LeavesTheExistingApplicationUntouched()
    {
        Store.SaveApplicationDefinition(new WorkbenchApplicationDefinition { ApplicationId = "catalog:package-hostile", DisplayName = "Keep me" });
        var bundle = HostileBundle(Payload(), hashOverride: _ => new string('0', 64));

        Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle, force: true));
        Assert.Equal("Keep me", Store.GetApplicationDefinition("catalog:package-hostile").DisplayName);
    }

    // ── Hostile bundles ────────────────────────────────────────

    [Fact]
    public void ABundleSchemaNewerThanThisBuild_IsRefused()
    {
        var bundle = HostileBundle(Payload(), schema: 99);
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.Contains("newer than this build supports", error.Message);
        Assert.False(AnyApplication(Store));
    }

    [Fact]
    public void AHashMismatch_IsRefused()
    {
        var bundle = HostileBundle(Payload(), hashOverride: _ => WorkbenchJson.TextSha256("something else"));
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.Contains("failed its hash check", error.Message);
        Assert.False(AnyApplication(Store));
    }

    [Fact]
    public void AnUnlistedExtraFile_IsRefused()
    {
        var bundle = HostileBundle(Payload(("application/profiles/p1/assets/0123456789abcdef/evil.ps1", "Write-Host pwned")),
            listed: ["application.json"]);
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.Contains("which its manifest does not list", error.Message);
        Assert.False(AnyApplication(Store));
    }

    [Fact]
    public void AnUnlistedFileOutsideThePayload_IsRefused()
    {
        var bundle = HostileBundle(Payload(("readme.txt", "extra")));
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.Contains("which its manifest does not list", error.Message);
    }

    [Fact]
    public void AListedEntryMissingFromTheArchive_IsRefused()
    {
        var bundle = HostileBundle(Payload(), listed: ["application.json", "profiles\\p1\\profile.json"]);
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.Contains("is missing from the archive", error.Message);
    }

    [Fact]
    public void ATraversingArchiveEntry_IsRefusedBeforeItIsWritten()
    {
        var marker = "apwb-escape-" + Guid.NewGuid().ToString("N") + ".txt";
        var bundle = HostileBundle(Payload(("application/../../" + marker, "escaped")), listed: ["application.json"]);

        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.Contains("escapes the bundle", error.Message);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), marker)));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetTempPath().TrimEnd('\\'))!, marker)));
        Assert.False(AnyApplication(Store));
    }

    [Theory]
    [InlineData(@"..\..\outside.txt", "escapes the bundle")]
    [InlineData(@"profiles\..\..\outside.txt", "escapes the bundle")]
    [InlineData(@"C:\Windows\win.ini", "is not a relative path")]
    [InlineData(@"\rooted.txt", "is not a relative path")]
    [InlineData(@"application.json:hidden", "alternate data stream")]
    [InlineData(@"profiles\NUL.txt", "device name")]
    public void AManifestEntryThatLeavesThePayload_IsRefused(string relative, string expected)
    {
        var bundle = HostileBundle(Payload(), listed: ["application.json", relative]);
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.Contains(expected, error.Message);
        Assert.False(AnyApplication(Store));
    }

    [Theory]
    [InlineData("application/application.json:hidden")]
    [InlineData("C:/rooted.txt")]
    [InlineData("application/CON")]
    public void AnArchiveEntryNamingAStreamDriveOrDevice_IsRefused(string name)
    {
        var bundle = HostileBundle(Payload((name, "x")), listed: ["application.json"]);
        Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.False(AnyApplication(Store));
    }

    [Fact]
    public void ADuplicatedArchiveEntry_IsRefused()
    {
        var path = BundlePath("dup.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var content in new[] { "first", "second" })
            {
                var entry = zip.CreateEntry("application/application.json");
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, path));
        Assert.Contains("appears twice", error.Message);
    }

    [Fact]
    public void ABundleWithoutAManifest_IsRefused()
    {
        var path = BundlePath("bare.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("application/application.json");
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, path));
        Assert.Contains("has no bundle.json", error.Message);
    }

    [Fact]
    public void AnInvalidApplicationIdInTheManifest_IsRefused()
    {
        var bundle = HostileBundle(Payload(), applicationId: @"catalog:..\..\evil");
        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(Store, bundle));
        Assert.Contains("Invalid ApplicationId", error.Message);
        Assert.False(AnyApplication(Store));
    }

    // ── Junctions ──────────────────────────────────────────────

    [Fact]
    public void Export_LeavesOutContentReachedThroughAJunction()
    {
        var id = NewByo();
        var outside = _t.NewFolder("outside");
        _t.NewFile(Path.Combine(outside, "secret.txt"), "not part of the application");
        Junction(Path.Combine(Store.GetApplicationFolder(id), "linked"), outside);

        var bundle = BundlePath();
        WorkbenchBundles.Export(Store, id, bundle, includeSources: true);

        var manifest = WorkbenchBundles.ReadManifest(bundle);
        Assert.DoesNotContain(manifest.Entries, e => e.RelativePath.Contains("secret", StringComparison.OrdinalIgnoreCase));
        using var zip = ZipFile.OpenRead(bundle);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Import_RefusesToReplaceAnApplicationFolderThatIsAJunction()
    {
        var id = NewByo();
        var bundle = BundlePath();
        WorkbenchBundles.Export(Store, id, bundle, includeSources: true);

        var outside = _t.NewFolder("victim");
        var victimFile = _t.NewFile(Path.Combine(outside, "keep.txt"), "must survive");
        var target = _second.GetApplicationFolder(id);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Junction(target, outside);

        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(_second, bundle, force: true));
        Assert.Contains("reparse point", error.Message);
        Assert.True(File.Exists(victimFile));
        Assert.False(File.Exists(Path.Combine(outside, "application.json")));
    }

    [Fact]
    public void Discovery_SkipsAByoFolderThatIsAJunction()
    {
        var real = NewByo();
        var outside = _t.NewFolder("elsewhere");
        _t.NewFile(Path.Combine(outside, "application.json"), """{"SchemaVersion":1,"ApplicationId":"byo:0123456789abcdef0123456789abcdef","DisplayName":"Linked","Origin":"byo"}""");
        Junction(Path.Combine(Store.ApplicationsRoot, "byo_0123456789abcdef0123456789abcdef"), outside);

        var byo = Store.DiscoverApplications().Where(a => a.Origin == ApplicationOrigin.Byo).Select(a => a.ApplicationId).ToList();
        Assert.Equal([real], byo);
    }

    [Fact]
    public void Import_RefusesAnApplicationsFolderThatIsAJunction()
    {
        var id = NewByo();
        var bundle = BundlePath();
        WorkbenchBundles.Export(Store, id, bundle, includeSources: true);

        var outside = _t.NewFolder("redirected");
        Directory.CreateDirectory(_second.DataRoot);
        Junction(_second.ApplicationsRoot, outside);

        var error = Assert.Throws<InvalidOperationException>(() => WorkbenchBundles.Import(_second, bundle));
        Assert.Contains("reparse point", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
}
