using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The runtime check of the installer: installer\Install-DesktopRuntime.ps1 decides whether the .NET Windows Desktop
/// Runtime at the minimum version is installed and checks a download before it runs it. The functions run in
/// Windows PowerShell 5.1, the shell the installer uses. None of these tests opens a window.
/// </summary>
public sealed class DesktopRuntimeScriptTests
{
    private static string RepoFile(params string[] parts)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "Install-DesktopRuntime.ps1"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine([folder.FullName, .. parts]);
    }

    private static string ScriptPath => RepoFile("installer", "Install-DesktopRuntime.ps1");

    private static async Task<(int ExitCode, string Output)> Run(string command)
    {
        var info = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // A test run started from PowerShell 7 (the CI shell) carries the module path of PowerShell 7, and Windows
        // PowerShell 5.1 then cannot load its own security module. The installer starts the script with a normal
        // environment; the child here gets the same.
        info.Environment.Remove("PSModulePath");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // The whole process tree ends, so no powershell.exe stays behind a failed run.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("powershell.exe did not end within 120 seconds.");
        }
        return (process.ExitCode, (await output + await error).Trim());
    }

    private static async Task<string> Call(string expression)
    {
        var (exit, output) = await Run($". '{ScriptPath}'; {expression}");
        Assert.True(exit == 0, output);
        return output;
    }

    [Theory]
    [InlineData("10.0.14", "10.0.14", true)]
    [InlineData("10.0.13", "10.0.14", false)]
    [InlineData("10.0.9", "10.0.14", false)]
    [InlineData("10.0.140", "10.0.14", true)]
    [InlineData("10.1.0", "10.0.14", true)]
    [InlineData("11.0.0", "10.0.14", true)]
    [InlineData("9.0.22", "10.0.14", false)]
    [InlineData("8.0.33", "10.0.14", false)]
    [InlineData("10.0.14-rc.1", "10.0.14", false)]
    [InlineData("11.0.0-preview.1", "10.0.14", false)]
    [InlineData("garbage", "10.0.14", false)]
    [InlineData("", "10.0.14", false)]
    public async Task OneInstalledVersionIsEnoughOnlyWhenItIsTheMinimumOrNewer(string installed, string minimum, bool expected) =>
        Assert.Equal(expected ? "True" : "False", await Call($"Test-DesktopRuntimeSatisfied -InstalledVersions @('{installed}') -Minimum '{minimum}'"));

    [Fact]
    public async Task ANewEnoughRuntimeAmongOldOnesAndGarbageIsFound() =>
        Assert.Equal("True", await Call("Test-DesktopRuntimeSatisfied -InstalledVersions @('6.0.36','8.0.33','x','10.0.14','9.0.22') -Minimum '10.0.14'"));

    [Fact]
    public async Task OnlyOldRuntimesAreNotEnough() =>
        Assert.Equal("False", await Call("Test-DesktopRuntimeSatisfied -InstalledVersions @('6.0.36','8.0.33','9.0.22','10.0.13') -Minimum '10.0.14'"));

    [Fact]
    public async Task NoRuntimeAtAllIsNotEnough() =>
        Assert.Equal("False", await Call("Test-DesktopRuntimeSatisfied -InstalledVersions @() -Minimum '10.0.14'"));

    [Fact]
    public async Task AMinimumThatIsNotAVersionIsAnError()
    {
        var (exit, output) = await Run($". '{ScriptPath}'; Test-DesktopRuntimeSatisfied -InstalledVersions @('10.0.14') -Minimum 'ten'");
        Assert.NotEqual(0, exit);
        Assert.Contains("not a version", output);
    }

    [Theory]
    [InlineData("https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe", true)]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe", true)]
    [InlineData("https://download.visualstudio.microsoft.com/download/pr/x/y/windowsdesktop-runtime-win-x64.exe", true)]
    [InlineData("http://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe", false)]
    [InlineData("https://example.com/windowsdesktop-runtime-win-x64.exe", false)]
    [InlineData("https://aka.ms.example.com/x.exe", false)]
    [InlineData("https://notmicrosoft.com/x.exe", false)]
    [InlineData("https://other.microsoft.com/x.exe", false)]
    [InlineData("https://contoso.azureedge.net/x.exe", false)]
    [InlineData("https://contoso.blob.core.windows.net/x.exe", false)]
    [InlineData("https://builds.dotnet.microsoft.com.example.com/x.exe", false)]
    public async Task OnlyAnHttpsAddressOfAVerifiedMicrosoftHostIsAccepted(string url, bool expected) =>
        Assert.Equal(expected ? "True" : "False", await Call($"Test-RuntimeDownloadUri ([uri]'{url}')"));

    [Theory]
    [InlineData("Microsoft Windows Desktop Runtime 10.0.14 (x64)", true)]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.33 (x64)", true)]
    [InlineData("Microsoft Windows Desktop Runtime 10.0.14 (x86)", false)]
    [InlineData("Microsoft .NET Runtime - 10.0.14 (x64)", false)]
    [InlineData("Microsoft ASP.NET Core 10.0.14 - Shared Framework (x64)", false)]
    [InlineData("Setup", false)]
    [InlineData("", false)]
    public async Task OnlyTheX64WindowsDesktopRuntimeInstallerHasTheRightProductName(string name, bool expected) =>
        Assert.Equal(expected ? "True" : "False", await Call($"Test-DesktopRuntimeProductName '{name}'"));

    [Theory]
    [InlineData("CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", true)]
    [InlineData("O=Microsoft Corporation, C=US", true)]
    [InlineData("CN=Contoso Ltd, O=Contoso Ltd, C=US", false)]
    [InlineData("CN=Microsoft Corporation, O=Contoso Ltd, C=US", false)]
    [InlineData("CN=Fake, O=Microsoft Corporation Fake, C=US", false)]
    [InlineData("", false)]
    public async Task OnlyAnOrganizationOfMicrosoftCorporationIsATrustedSigner(string subject, bool expected) =>
        Assert.Equal(expected ? "True" : "False", await Call($"Test-MicrosoftSignerSubject '{subject}'"));

    [Fact]
    public async Task TheRuntimeFoldersOfAnInstallRootAreListedAndOtherNamesAreKept()
    {
        var root = Path.Combine(Path.GetTempPath(), "apd-dotnet-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shared = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            foreach (var name in new[] { "10.0.14", "9.0.22", "11.0.0-rc.1" }) Directory.CreateDirectory(Path.Combine(shared, name));
            File.WriteAllText(Path.Combine(shared, "stray.txt"), "x");
            Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.14"));

            var listed = await Call($"(Get-DesktopRuntimeVersionsInRoot -Root '{root}' | Sort-Object) -join ','");
            Assert.Equal("10.0.14,11.0.0-rc.1,9.0.22", listed);
            Assert.Equal("True", await Call($"Test-DesktopRuntimeSatisfied -InstalledVersions (Get-DesktopRuntimeVersionsInRoot -Root '{root}') -Minimum '10.0.14'"));
            Assert.Equal("False", await Call($"Test-DesktopRuntimeSatisfied -InstalledVersions (Get-DesktopRuntimeVersionsInRoot -Root '{root}') -Minimum '10.0.15'"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\does\\not\\exist")]
    public async Task AMissingInstallRootHasNoRuntimes(string root) =>
        Assert.Equal("0", await Call($"@(Get-DesktopRuntimeVersionsInRoot -Root '{root}').Count"));

    [Fact]
    public async Task ADownloadFromAnAddressThatIsNotAnHttpsAddressOfMicrosoftSavesNothing()
    {
        var probe = System.Net.Sockets.TcpListener.Create(0);
        probe.Start();
        var number = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{number}/");
        listener.Start();
        var served = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.OutputStream.Write([(byte)'M', (byte)'Z', 1, 2, 3, 4]);
            context.Response.Close();
        });
        var file = Path.Combine(Path.GetTempPath(), "apd-save-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var result = await Call($"$r = Save-RuntimeDownload -Url 'http://127.0.0.1:{number}/x.exe' -Path '{file}'; if ($null -eq $r) {{ 'null' }} else {{ 'not null' }}");
            await served;

            Assert.Equal("null", result);
            Assert.False(File.Exists(file));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task AnUnsignedFileIsNotTrusted()
    {
        var file = Path.Combine(Path.GetTempPath(), "apd-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(file, [(byte)'M', (byte)'Z', 1, 2, 3, 4, 5, 6]);
        try
        {
            var (exit, output) = await Run($". '{ScriptPath}'; Assert-MicrosoftSigned -Path '{file}'");
            Assert.NotEqual(0, exit);
            Assert.Contains("signature", output);
        }
        finally { File.Delete(file); }
    }

    // A file that every Windows has and that carries a valid Microsoft signature: the Windows PowerShell executable,
    // which this test starts itself. A missing file fails the test; it is never skipped.
    private static string WindowsPowerShell()
    {
        var path = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(path), $"The test needs {path}.");
        return path;
    }

    [Fact]
    public async Task AFileWithAValidSignatureOfMicrosoftCorporationIsTrustedAsASigner()
    {
        var signed = WindowsPowerShell();
        var (signatureExit, status) = await Run($"(Get-AuthenticodeSignature -LiteralPath '{signed}').Status");
        Assert.True(signatureExit == 0 && status == "Valid", $"The test file has no valid signature: {status}");

        var (exit, output) = await Run($". '{ScriptPath}'; Assert-MicrosoftSigned -Path '{signed}'; 'trusted'");
        Assert.True(exit == 0, output);
        Assert.Equal("trusted", output);
    }

    [Fact]
    public async Task AValidMicrosoftFileThatIsNotTheDesktopRuntimeInstallerIsNotTrusted()
    {
        var signed = WindowsPowerShell();

        var (exit, output) = await Run($". '{ScriptPath}'; Assert-RuntimeInstallerTrusted -Path '{signed}' -Minimum '1.0.0'");

        Assert.NotEqual(0, exit);
        Assert.Contains("not the x64 Windows Desktop Runtime installer", output);
    }

    [Fact]
    public async Task TheCheckOnlyRunAnswersFromTheInstalledRuntimes()
    {
        // A minimum no machine has: 1, and the text names the minimum.
        var (exit, output) = await Run($"& '{ScriptPath}' -MinimumVersion 999.0.0 -CheckOnly; exit $LASTEXITCODE");
        Assert.Equal(1, exit);
        Assert.Contains("999.0.0 or newer (x64) is not installed", output);
    }

    [Fact]
    public void TheMinimumVersionIsOneValueInTheProjectTheScriptAndTheInstallerBuild()
    {
        var props = File.ReadAllText(RepoFile("src", "DesktopRuntime.props"));
        var minimum = Regex.Match(props, @"<DesktopRuntimeMinimum>([^<]+)</DesktopRuntimeMinimum>").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", minimum);
        Assert.Contains("<RuntimeFrameworkVersion>$(DesktopRuntimeMinimum)</RuntimeFrameworkVersion>", props);

        var script = File.ReadAllText(ScriptPath);
        Assert.Contains($"[string]$MinimumVersion = '{minimum}'", script);

        var build = File.ReadAllText(RepoFile("tools", "build-installer.ps1"));
        Assert.Contains("DesktopRuntimeMinimum", build);
        Assert.Contains("/DRUNTIME_MIN=", build);

        var nsi = File.ReadAllText(RepoFile("installer", "AppPackagerDesktop.nsi"));
        Assert.Contains("Install-DesktopRuntime.ps1", nsi);
        Assert.Contains("RUNTIME_MIN", nsi);
    }

    [Fact]
    public void BothExecutablesImportTheRuntimeMinimumAndTheReleaseIsNotSelfContained()
    {
        foreach (var project in new[] { "ApplicationPackagerUI", "ApplicationPackagerBuild" })
            Assert.Contains("DesktopRuntime.props", File.ReadAllText(RepoFile("src", project, project + ".csproj")));

        var release = File.ReadAllText(RepoFile("tools", "build-release.ps1"));
        Assert.Contains("--self-contained false", release);
        Assert.DoesNotContain("--self-contained true", release);
        Assert.DoesNotContain("IncludeNativeLibrariesForSelfExtract", release);
    }
}
