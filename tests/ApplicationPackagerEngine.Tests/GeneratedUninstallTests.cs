using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Tests.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A generated EXE uninstall never starts the staged installer: an installer started with no arguments, or with
/// arguments nobody proved, waits for input in session 0 until the client times out. With no uninstall command and no
/// silent uninstall arguments the script exits non-zero and says why, and the review marks the definition.
/// </summary>
public sealed class GeneratedUninstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apd-generated-uninstall-" + Guid.NewGuid().ToString("N"));
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public GeneratedUninstallTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        TestCleanup.DeleteDirectory(_dir);
    }

    private const string Installer = "contoso-setup.exe";

    /// <summary>A display name that no Add/Remove Programs entry carries.</summary>
    private static readonly string NoSuchProduct = "Zz No Such Product " + Guid.NewGuid().ToString("N");

    private string Uninstall(string? framework, string uninstallArgs)
    {
        new WrapperGeneratorService().GenerateExeWrappers(_dir, Installer, "/S", uninstallArgs, framework, NoSuchProduct);
        return File.ReadAllText(Path.Combine(_dir, "uninstall.ps1"));
    }

    private async Task<(int ExitCode, string StdOut)> RunUninstallAsync()
    {
        var (exitCode, stdOut, _) = await WindowsPowerShellHost.RunAsync(
            $"& '{Path.Combine(_dir, "uninstall.ps1")}'\r\nexit $LASTEXITCODE", null, CancellationToken.None, "apd-generated-uninstall");
        return (exitCode, stdOut);
    }

    public static TheoryData<string?> Frameworks => new() { null, "Unknown", "InstallShield", "AdvancedInstaller", "WixBurn", "InnoSetup", "NSIS", "SevenZipSfx" };

    [Theory]
    [MemberData(nameof(Frameworks))]
    public async Task WithNoUninstallCommandAndNoUninstallArguments_TheScriptExitsNonZeroAndSaysWhy(string? framework)
    {
        var script = Uninstall(framework, uninstallArgs: "");

        Assert.DoesNotContain(Installer, script);
        Assert.DoesNotContain("Start-Process", script);
        var (exitCode, stdOut) = await RunUninstallAsync();
        Assert.NotEqual(0, exitCode);
        Assert.Contains("Nothing was uninstalled", stdOut);
        Assert.Contains("uninstall command", stdOut);
    }

    [Theory]
    [MemberData(nameof(Frameworks))]
    public async Task WhenNoAddRemoveProgramsEntryMatches_TheScriptNeverStartsTheStagedInstaller(string? framework)
    {
        var script = Uninstall(framework, uninstallArgs: "/uninstall /quiet");

        Assert.DoesNotContain(Installer, script);
        var (exitCode, stdOut) = await RunUninstallAsync();
        Assert.NotEqual(0, exitCode);
        Assert.Contains("nothing was uninstalled", stdOut);
    }

    [Fact]
    public async Task TheReviewOfAnUnknownExe_SaysThatNoUninstallIsKnown_AndTheSaveResultNeedsReview()
    {
        using var bed = new AddFromInstallerTestBed();
        var service = bed.Real();
        var r = await service.AnalyzeAsync(bed.Marked("mystery.exe", null), NoProgress, CancellationToken.None);

        Assert.Contains(AddFromInstallerService.NoUninstallReason, r.ReviewReason);

        var edited = r with
        {
            Name = new ProposedValue<string?>(Value: "Mystery Tool", Evidence: Evidence.Manual, Source: null),
            Version = new ProposedValue<string?>(Value: "1.0", Evidence: Evidence.Manual, Source: null),
            InstallArguments = new ProposedValue<string>(Value: "/silent", Evidence: Evidence.Manual, Source: null)
        };
        var saved = await service.SaveAsync(edited, new AddSaveOptions(DefinitionsFolder: bed.Definitions), CancellationToken.None);
        Assert.True(saved.NeedsReview);
        Assert.Contains(AddFromInstallerService.NoUninstallReason, saved.ReviewReason);

        var withUninstall = edited with
        {
            Name = new ProposedValue<string?>(Value: "Mystery Tool Two", Evidence: Evidence.Manual, Source: null),
            UninstallArguments = new ProposedValue<string>(Value: "/silent", Evidence: Evidence.Manual, Source: null)
        };
        var savedWithUninstall = await service.SaveAsync(withUninstall, new AddSaveOptions(DefinitionsFolder: bed.Definitions), CancellationToken.None);
        Assert.DoesNotContain(AddFromInstallerService.NoUninstallReason, savedWithUninstall.ReviewReason ?? "");
        Assert.Equal("/silent", JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(savedWithUninstall.FilePath))!.UninstallArgs);
    }

    [Fact]
    public async Task TheReviewOfAnInstallerWithAnUninstallCommand_DoesNotSayThatNoUninstallIsKnown()
    {
        using var bed = new AddFromInstallerTestBed();
        var r = await bed.Real().AnalyzeAsync(bed.Marked("isetup.exe", "InstallShield"), NoProgress, CancellationToken.None);

        Assert.DoesNotContain(AddFromInstallerService.NoUninstallReason, r.ReviewReason ?? "");
    }
}
