using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using Xunit;

namespace ApplicationPackagerEngine.Tests;

public sealed class ManifestSchemaV3Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "packr-v3-" + Guid.NewGuid().ToString("N"));
    private readonly ManifestService _service = new();

    public ManifestSchemaV3Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string WriteContent(string relative, string text)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    [Fact]
    public void Write_RecordsRecursiveFileHashes_AndVerifies()
    {
        WriteContent("installer.msi", "payload");
        WriteContent(Path.Combine("arm64", "installer-arm64.exe"), "arm-payload");
        var manifestPath = Path.Combine(_root, "stage-manifest.json");

        _service.Write(manifestPath, new StageManifest { AppName = "A" });

        var read = _service.Read(manifestPath);
        Assert.Equal(4, read.SchemaVersion);
        Assert.NotNull(read.FileHashes);
        Assert.Equal(2, read.FileHashes!.Length);
        Assert.Contains(read.FileHashes, h => h.RelativePath.Replace('\\', '/') == "arm64/installer-arm64.exe");
        Assert.All(read.FileHashes, h => Assert.Equal(64, h.Sha256.Length));
    }

    [Fact]
    public void Verify_FlagsTamperedMissingAndExtraFiles()
    {
        WriteContent("a.txt", "one");
        var manifestPath = Path.Combine(_root, "stage-manifest.json");
        _service.Write(manifestPath, new StageManifest { AppName = "A" });
        var manifest = _service.Read(manifestPath);

        File.WriteAllText(Path.Combine(_root, "a.txt"), "tampered");
        WriteContent("extra.txt", "extra");

        var problems = ManifestService.VerifyFileHashes(_root, manifest.FileHashes, "stage-manifest.json");
        Assert.Contains(problems, p => p.Contains("hash mismatch"));
        Assert.Contains(problems, p => p.Contains("unexpected file"));

        File.Delete(Path.Combine(_root, "a.txt"));
        problems = ManifestService.VerifyFileHashes(_root, manifest.FileHashes, "stage-manifest.json");
        Assert.Contains(problems, p => p.Contains("missing"));
    }

    [Fact]
    public void Read_V2ManifestWithoutNewFields_Deserializes()
    {
        var manifestPath = Path.Combine(_root, "stage-manifest.json");
        File.WriteAllText(manifestPath, """{"SchemaVersion":2,"AppName":"Old","Publisher":"P","SoftwareVersion":"1.0"}""");

        var read = _service.Read(manifestPath);
        Assert.Equal("Old", read.AppName);
        Assert.Null(read.FileHashes);
        Assert.Null(read.DeploymentTypes);
        Assert.Null(read.CommandOverrides);
    }

    [Fact]
    public void Read_PsV3Manifest_ParsesDeploymentTypesAndOverrides()
    {
        // The schema version 3 shape a PS1 packager writes through AppPackagerCommon 0.0.21.
        var manifestPath = Path.Combine(_root, "stage-manifest.json");
        File.WriteAllText(manifestPath, """
        {
          "SchemaVersion": 3,
          "AppName": "App",
          "CommandOverrides": { "Install": "setup.exe /s", "Uninstall": "" },
          "DeploymentTypes": [
            { "NameSuffix": "ARM64", "ContentSubpath": "arm64",
              "Requirements": [ { "ConditionId": "cpu-arch", "Value": "ARM64" } ] },
            { "NameSuffix": "x64",
              "Requirements": [ { "ConditionId": "os-language", "Cultures": ["de-DE","fr-FR"] } ] }
          ]
        }
        """);

        var read = _service.Read(manifestPath);
        Assert.Equal(2, read.DeploymentTypes!.Length);
        Assert.Equal("arm64", read.DeploymentTypes[0].ContentSubpath);
        Assert.Equal("cpu-arch", read.DeploymentTypes[0].Requirements![0].ConditionId);
        Assert.Equal(new[] { "de-DE", "fr-FR" }, read.DeploymentTypes[1].Requirements![0].Cultures);
        Assert.Equal("setup.exe /s", read.CommandOverrides!.Install);
    }
}
