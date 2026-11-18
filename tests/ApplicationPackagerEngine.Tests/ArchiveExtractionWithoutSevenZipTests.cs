using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A PC without 7-Zip opens the App Packager page (found on the lab client in the Q-53 check of the real page, which has no 7-Zip): the service that reads
/// archives is a dependency of the page and used to throw when it was made. It now looks for the tool when an archive is read, and says so in the result.
/// </summary>
public sealed class ArchiveExtractionWithoutSevenZipTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-no7z-" + Guid.NewGuid().ToString("N"));

    public ArchiveExtractionWithoutSevenZipTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string Installer()
    {
        var path = Path.Combine(_folder, "setup.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0, 0]);
        return path;
    }

    [Fact]
    public void TheServiceIsMadeOnAPcWithoutSevenZip()
    {
        var service = new ArchiveExtractionService { FindTool = () => null };

        Assert.NotNull(service);
    }

    [Fact]
    public async Task AnExtractionOnAPcWithoutSevenZipFailsWithTheMessageThatNamesTheTool()
    {
        var service = new ArchiveExtractionService { FindTool = () => null };

        using var result = await service.ExtractAsync(Installer());

        Assert.False(result.Success);
        Assert.Equal(ArchiveExtractionService.MissingSevenZipMessage, result.ErrorMessage);
        Assert.Contains("7-Zip", result.ErrorMessage);
    }

    [Fact]
    public async Task AListingOnAPcWithoutSevenZipFailsWithTheMessageThatNamesTheTool()
    {
        var service = new ArchiveExtractionService { FindTool = () => null };

        var result = await service.ListAsync(Installer());

        Assert.False(result.Success);
        Assert.Equal(ArchiveExtractionService.MissingSevenZipMessage, result.ErrorMessage);
    }

    [Fact]
    public async Task APathThatTheCallerGaveIsUsedWithoutTheLookup()
    {
        var looked = false;
        var service = new ArchiveExtractionService("7z-not-used.exe") { FindTool = () => { looked = true; return null; } };

        using var result = await service.ExtractAsync(Installer());

        Assert.False(looked);
        Assert.NotEqual(ArchiveExtractionService.MissingSevenZipMessage, result.ErrorMessage);
    }
}
