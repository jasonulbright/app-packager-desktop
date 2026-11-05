using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using static ApplicationPackagerEngine.Tests.AddFromInstaller.AddFromInstallerTestBed;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>
/// Q-01: an Advanced Installer or InstallShield EXE that wraps an MSI gives the ProductCode of the wrapped MSI.
/// Q-03: Add from file copies the installer at save time and verifies the copy by hash.
/// </summary>
public sealed class WrappedMsiAndInstallerCopyTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public void Dispose() => _bed.Dispose();

    private static AppDefinition Read(string path) => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path))!;

    /// <summary>A PE-looking file that carries the engine marker, with a 7-Zip archive of the given files appended.</summary>
    private string WrapperExe(string marker, params string[] files)
    {
        var sevenZip = ArchiveExtractionService.FindSevenZip();
        Assert.True(sevenZip is not null, "7-Zip is not installed on this machine; install 7-Zip 25.01 or later to run this test.");
        var archive = _bed.PathOf("payload-" + Guid.NewGuid().ToString("N") + ".7z");
        var start = new ProcessStartInfo(sevenZip!) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("a");
        start.ArgumentList.Add(archive);
        foreach (var file in files) start.ArgumentList.Add(file);
        using (var process = Process.Start(start)!)
        {
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        var head = new List<byte> { (byte)'M', (byte)'Z' };
        head.AddRange(Enumerable.Repeat((byte)0, 510));
        head.AddRange(Encoding.ASCII.GetBytes(marker));
        head.AddRange(Enumerable.Repeat((byte)0, 64));
        var exe = _bed.PathOf("setup-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(exe, [.. head, .. File.ReadAllBytes(archive)]);
        return exe;
    }

    [Theory]
    [InlineData("InstallShield")]
    [InlineData("Advanced Installer")]
    public async Task AnExeThatWrapsAnMsi_GivesTheProductCodeOfTheExtractedMsi(string marker)
    {
        var exe = WrapperExe(marker, _bed.Msi("wrapped.msi"));

        var r = await _bed.Real().AnalyzeAsync(exe, NoProgress, CancellationToken.None);

        Assert.NotNull(r.ProductCode);
        Assert.Equal(ProductCode, r.ProductCode!.Value, ignoreCase: true);
        Assert.Equal(Evidence.Confirmed, r.ProductCode.Evidence);
        Assert.Equal("msi-property-table", r.ProductCode.Source);
        Assert.Contains(ProductCode, r.UninstallRegistryKey!.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnExeWithNoMsiInside_KeepsNoProductCodeAndSaysWhy()
    {
        var other = _bed.PathOf("readme.txt");
        File.WriteAllText(other, "hello");
        var exe = WrapperExe("InstallShield", other);

        var r = await _bed.Real().AnalyzeAsync(exe, NoProgress, CancellationToken.None);

        Assert.True(r.ProductCode is null || string.IsNullOrEmpty(r.ProductCode.Value));
        Assert.Contains(r.Notes, n => n.Contains("no MSI is in the payload"));
    }

    // ---- Q-03 ---------------------------------------------------------------------------------

    private async Task<(AppDefinition Definition, string Installer)> SavedAsync(string installer, string name = "Contoso Tool")
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(installer, NoProgress, CancellationToken.None);
        r = r with { Name = r.Name with { Value = name, Evidence = Evidence.Manual } };
        var saved = await service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None);
        return (Read(saved.FilePath), installer);
    }

    [Fact]
    public async Task Save_CopiesTheInstallerAndTheDefinitionPointsAtTheVerifiedCopy()
    {
        var (def, installer) = await SavedAsync(_bed.Msi());

        var copy = def.LocalInstaller!.Path;
        Assert.NotEqual(Path.GetFullPath(installer), copy);
        Assert.StartsWith(Path.Combine(_bed.Root, AddFromInstallerService.InstallerCopyFolderName), copy, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StagingService.ComputeSha256(installer), StagingService.ComputeSha256(copy));
        Assert.Equal(def.LocalInstaller.Sha256, StagingService.ComputeSha256(copy), ignoreCase: true);
        Assert.Equal("1", Path.GetFileName(Path.GetDirectoryName(copy)));

        File.Delete(installer);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void ACopyNeverOverwritesAnEarlierRevision()
    {
        var installer = _bed.Msi();
        var source = new LocalInstallerConfig { Path = installer, Sha256 = StagingService.ComputeSha256(installer), Version = "4.5.6" };

        var first = AddFromInstallerService.StoreInstallerCopy(_bed.Definitions, "Contoso Tool", source).Copy;
        File.WriteAllText(first.Path, "kept");
        var second = AddFromInstallerService.StoreInstallerCopy(_bed.Definitions, "Contoso Tool", source).Copy;

        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal("kept", File.ReadAllText(first.Path));
        Assert.Equal("2", Path.GetFileName(Path.GetDirectoryName(second.Path)));
        Assert.Equal(source.Sha256, StagingService.ComputeSha256(second.Path), ignoreCase: true);
    }

    [Fact]
    public void ACopyWhoseHashDiffersFromTheAnalysedHash_IsRefusedAndLeavesNoFile()
    {
        var installer = _bed.Msi();
        var source = new LocalInstallerConfig { Path = installer, Sha256 = new string('0', 64), Version = "4.5.6" };

        Assert.Throws<AddFromInstallerRefusedException>(() => AddFromInstallerService.StoreInstallerCopy(_bed.Definitions, "Contoso Tool", source));

        var root = Path.Combine(_bed.Root, AddFromInstallerService.InstallerCopyFolderName);
        Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : []);
    }
}
