using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Source files, stage copies and the stage prune never read, write or delete through
/// a directory junction, and a source destination cannot name an alternate data
/// stream or a device.
/// </summary>
public sealed class WorkbenchReparseHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apwb-reparse-" + Guid.NewGuid().ToString("N"));

    public WorkbenchReparseHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Junctions are removed first so the recursive delete never walks into a target.
        foreach (var link in Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories)
                     .Where(WorkbenchJson.IsReparsePoint).OrderByDescending(p => p.Length).ToList())
            try { Directory.Delete(link); } catch (IOException) { }
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string NewFolder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
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

    [Fact]
    public void APickedFolderIsNotEnumeratedThroughAJunction()
    {
        var outside = NewFolder("outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside the picked folder");
        var picked = NewFolder("picked");
        File.WriteAllText(Path.Combine(picked, "readme.txt"), "inside");
        Directory.CreateDirectory(Path.Combine(picked, "sub"));
        File.WriteAllText(Path.Combine(picked, "sub", "a.txt"), "inside");
        Junction(Path.Combine(picked, "linked"), outside);

        var entries = WorkbenchSourceFiles.FromPath(picked);

        Assert.Equal([@"picked\readme.txt", @"picked\sub\a.txt"], entries.Select(e => e.Destination).ToArray());
        Assert.DoesNotContain(entries, e => e.Provenance.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFileWhoseFolderInsideThePickedFolderIsAJunctionIsRefused()
    {
        var outside = NewFolder("outside2");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
        var picked = NewFolder("picked2");
        Junction(Path.Combine(picked, "linked"), outside);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkbenchSourceFiles.AssertNoReparseAncestor(picked, Path.Combine(picked, "linked", "secret.txt")));
        Assert.Contains("reparse point", ex.Message);
    }

    [Theory]
    [InlineData("install.ps1:hidden")]
    [InlineData(@"tools\a.txt:Zone.Identifier")]
    [InlineData("NUL")]
    [InlineData(@"tools\con.txt")]
    [InlineData("COM1")]
    [InlineData(@"LPT9\x.txt")]
    public void ADestinationWithAStreamOrADeviceNameIsRefusedByTheCopierAndTheValidator(string destination)
    {
        var stage = NewFolder("stage-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(stage, "install.ps1"), "generated");
        var source = Path.Combine(NewFolder("src-" + Guid.NewGuid().ToString("N")[..6]), "a.txt");
        File.WriteAllText(source, "payload");

        Assert.Throws<InvalidOperationException>(() => StageFileCopier.Copy(stage, source, destination));
        Assert.Equal("generated", File.ReadAllText(Path.Combine(stage, "install.ps1")));

        var profile = new JsonObject
        {
            ["SourceFiles"] = new JsonArray(new JsonObject { ["Destination"] = destination, ["Asset"] = "x" })
        };
        Assert.Throws<InvalidOperationException>(() => WorkbenchProfileValidator.Validate(profile));
    }

    [Theory]
    [InlineData(@"tools\console.txt")]
    [InlineData("nul-device-notes.txt")]
    [InlineData(@"config\settings.ini")]
    public void AnOrdinaryDestinationPasses(string destination)
    {
        StageFileCopier.AssertPlainDestination(destination);
        WorkbenchProfileValidator.Validate(new JsonObject
        {
            ["SourceFiles"] = new JsonArray(new JsonObject { ["Destination"] = destination, ["Asset"] = "x" })
        });
    }

    [Fact]
    public void ThePruneNeverDeletesThroughAFolderReplacedByAJunction()
    {
        const string applicationId = "catalog:package-prunetest";
        var dataRoot = NewFolder("data");
        var stage = NewFolder("stage");
        var outside = NewFolder("victim");
        var victim = Path.Combine(outside, "keep.txt");
        File.WriteAllText(victim, "outside the stage root");
        BuildRecords.Write(dataRoot, stage, new StageManifest
        {
            ApplicationId = applicationId,
            BuildId = "20261003-120000-aaaaaaaa",
            ProfileId = "pilot001",
            CustomAssets =
            [
                new StageCustomAsset { Category = "SourceFile", RelativePath = @"tools\keep.txt", Sha256 = WorkbenchJson.FileSha256(victim) },
                new StageCustomAsset { Category = "InstallBefore", RelativePath = @"tools\install-before.ps1" }
            ]
        });
        File.WriteAllText(Path.Combine(outside, "install-before.ps1"), "x");
        Junction(Path.Combine(stage, "tools"), outside);
        var log = new List<string>();

        var removed = StaleStageArtifacts.Prune(stage, dataRoot, applicationId, "pilot001", "20261003-130000-bbbbbbbb", log.Add);

        Assert.Empty(removed);
        Assert.True(File.Exists(victim));
        Assert.True(File.Exists(Path.Combine(outside, "install-before.ps1")));
        Assert.True(WorkbenchJson.IsReparsePoint(Path.Combine(stage, "tools")));
        Assert.Contains(log, l => l.Contains("reparse point"));
    }

    [Fact]
    public void ThePruneStillRemovesARealStaleFileAndItsEmptyFolder()
    {
        const string applicationId = "catalog:package-prunetest2";
        var dataRoot = NewFolder("data2");
        var stage = NewFolder("stage2");
        Directory.CreateDirectory(Path.Combine(stage, "tools"));
        var stale = Path.Combine(stage, "tools", "old.txt");
        File.WriteAllText(stale, "old");
        BuildRecords.Write(dataRoot, stage, new StageManifest
        {
            ApplicationId = applicationId,
            BuildId = "20261003-120000-aaaaaaaa",
            ProfileId = "pilot001",
            CustomAssets = [new StageCustomAsset { Category = "SourceFile", RelativePath = @"tools\old.txt", Sha256 = WorkbenchJson.FileSha256(stale) }]
        });

        var removed = StaleStageArtifacts.Prune(stage, dataRoot, applicationId, "pilot001", "20261003-130000-bbbbbbbb");

        Assert.Equal([@"tools\old.txt"], removed);
        Assert.False(Directory.Exists(Path.Combine(stage, "tools")));
    }
}
