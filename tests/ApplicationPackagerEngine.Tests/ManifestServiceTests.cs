using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class ManifestServiceTests : IDisposable
{
    private readonly ManifestService _svc = new();
    private readonly string _tempDir;

    public ManifestServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"packr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void WriteAndRead_RoundTrips()
    {
        var path = Path.Combine(_tempDir, "manifest.json");
        var manifest = new StageManifest
        {
            AppName = "7-Zip",
            SoftwareVersion = "24.09",
            Publisher = "Igor Pavlov",
            InstallerFile = "7zip-x64.msi"
        };

        _svc.Write(path, manifest);
        var loaded = _svc.Read(path);

        Assert.Equal("7-Zip", loaded.AppName);
        Assert.Equal("24.09", loaded.SoftwareVersion);
        Assert.Equal("Igor Pavlov", loaded.Publisher);
    }

    [Fact]
    public void Write_SetsStagedAtTimestamp()
    {
        var path = Path.Combine(_tempDir, "manifest2.json");
        var before = DateTimeOffset.Now.AddSeconds(-1);

        _svc.Write(path, new StageManifest { AppName = "Test" });

        var loaded = _svc.Read(path);
        Assert.True(loaded.StagedAt >= before);
    }

    [Fact]
    public void Read_MissingFile_ThrowsFileNotFound()
    {
        var ex = Assert.Throws<FileNotFoundException>(() =>
            _svc.Read(Path.Combine(_tempDir, "nonexistent.json")));
        Assert.Contains("manifest", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
