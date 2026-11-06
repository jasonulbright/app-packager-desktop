using System.Diagnostics;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Unsafe defaults the PowerShell app removed in commit c97d69f: a fixed database
/// password in every target, a payload over plain HTTP, a runtime detected through
/// a file the runtime does not own, and a rebuilt installer that is no new release.
/// </summary>
public sealed class SecurityDefaultsDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-secdef-" + Guid.NewGuid().ToString("N"));

    public SecurityDefaultsDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static IEnumerable<string> DefinitionFiles() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json").Select(Path.GetFileName)!;

    public static TheoryData<string> AllDefinitions()
    {
        var data = new TheoryData<string>();
        foreach (var name in DefinitionFiles()) data.Add(name);
        return data;
    }

    public static TheoryData<int> PostgresMajors() => new() { 13, 14, 15, 16, 17 };

    // ---- a fixed password --------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDefinitions))]
    public void NoDefinitionCarriesAPasswordValueInItsArguments(string file)
    {
        var def = DefinitionFixture.Load(file);
        var texts = new List<string?> { def.InstallArgs, def.UninstallArgs, def.UninstallCommand };
        foreach (var p in def.InstallerSettings ?? [])
            texts.AddRange([p.CommandFragment, p.UninstallFragment, p.DefaultValue]);

        // A password switch or property is followed by the token, by a setting or by nothing.
        var literal = new Regex(@"(?i)(?:password|passwd)[= ]\s*(?![{\s]|$)\S+");
        foreach (var text in texts.Where(t => !string.IsNullOrEmpty(t)))
            Assert.False(literal.IsMatch(text!), $"{file} carries a literal password value: {literal.Match(text!).Value.Split(' ', '=')[0]}");
    }

    [Theory]
    [MemberData(nameof(PostgresMajors))]
    public void PostgreSqlTakesASecretEachTargetGenerates(int major)
    {
        var def = DefinitionFixture.Load($"postgresql{major}.json");
        Assert.Contains("--superpassword {TargetSecret} ", def.InstallArgs);
        Assert.Equal("PostgreSQL-Superuser-Secret", def.TargetSecret!.FolderName);
        Assert.Equal($"postgresql-{major}.dpapi", def.TargetSecret.FileName);

        var script = StagedInstallScript(def);
        Assert.DoesNotContain(TargetSecretScript.Token, script);
        Assert.Contains("--superpassword ' + $targetSecret + ' --serverport 5432", script);
        Assert.StartsWith("try {", script);
        Assert.True(script.IndexOf("$secretFile", StringComparison.Ordinal) < script.IndexOf("Start-Process", StringComparison.Ordinal));
    }

    [Fact]
    public void MariaDbTakesARootPasswordOnlyWhenItInstallsAService()
    {
        var def = DefinitionFixture.Load("mariadb-server.json");
        Assert.Equal("MariaDB-Root-Secret", def.TargetSecret!.FolderName);

        var withService = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, null);
        Assert.Contains("SERVICENAME=MariaDB PASSWORD={TargetSecret}", withService);

        var toolsOnly = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings,
            new Dictionary<string, string> { ["ServiceName"] = "" });
        Assert.DoesNotContain("PASSWORD", toolsOnly);
    }

    [Fact]
    public void TheMariaDbMsiWrapperJoinsTheGeneratedPassword()
    {
        var def = DefinitionFixture.Load("mariadb-server.json");
        var args = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, null);
        new WrapperGeneratorService().GenerateMsiWrappers(_dir, "mariadb.msi", null, args);
        var script = TargetSecretScript.Apply(File.ReadAllText(Path.Combine(_dir, "install.ps1")), def.TargetSecret!);

        Assert.DoesNotContain(TargetSecretScript.Token, script);
        Assert.Contains("PASSWORD=' + $targetSecret + ' PORT=3306", script);
    }

    [Fact]
    public void TheManifestNamesNoSecret()
    {
        Assert.DoesNotContain("{", TargetSecretScript.ManifestText);
        Assert.Equal("--superpassword <generated on each target> --x",
            "--superpassword {TargetSecret} --x".Replace(TargetSecretScript.Token, TargetSecretScript.ManifestText));
    }

    [Theory]
    [InlineData("bad'name", "a.dpapi", "x")]
    [InlineData("Folder", "a b.dpapi", "x")]
    [InlineData("Folder", "a.dpapi", "x'; calc")]
    [InlineData("..", "a.dpapi", "x")]
    public void AnUnusableSecretLocationIsRefused(string folder, string file, string label) =>
        Assert.Throws<InvalidOperationException>(() => TargetSecretScript.BuildBlock(
            new TargetSecretConfig { FolderName = folder, FileName = file, Label = label }));

    [Fact]
    public void ASecretThatNoArgumentUsesIsRefused()
    {
        var config = new TargetSecretConfig { FolderName = "F", FileName = "f.dpapi" };
        var ex = Assert.Throws<InvalidOperationException>(() => TargetSecretScript.Apply("$proc = Start-Process 'setup.exe'", config));
        Assert.Contains(TargetSecretScript.Token, ex.Message);
    }

    [Fact]
    public void OnlyTheLineWithTheTokenIsRewritten()
    {
        var config = new TargetSecretConfig { FolderName = "F", FileName = "f.dpapi" };
        var script = TargetSecretScript.Apply("# it's a comment\n$p = Start-Process 'a.exe' -ArgumentList 'x {TargetSecret} y' -Wait\n$q = 'z'", config);
        Assert.Contains("-ArgumentList ('x ' + $targetSecret + ' y') -Wait", script);
        Assert.EndsWith("$q = 'z'", script);
        Assert.Contains("# it's a comment", script);
    }

    private string StagedInstallScript(AppDefinition def)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        new WrapperGeneratorService().GenerateExeWrappers(dir, "setup.exe", def.InstallArgs, def.UninstallArgs,
            def.InstallerFramework, def.Application, def.UninstallCommand);
        return TargetSecretScript.Apply(File.ReadAllText(Path.Combine(dir, "install.ps1")), def.TargetSecret!);
    }

    // ---- the secret block, run in Windows PowerShell -----------------------

    private sealed record BlockRun(int ExitCode, string[] Output, string Acl);

    private BlockRun RunBlock(TargetSecretConfig config, string programData)
    {
        Directory.CreateDirectory(programData);
        var script = Path.Combine(programData, $"probe-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, TargetSecretScript.BuildBlock(config) + "\r\nWrite-Output ('PW=' + $targetSecret)\r\n");
        var start = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        // A PSModulePath inherited from PowerShell 7 keeps Get-Acl from loading in Windows PowerShell 5.1.
        WindowsPowerShellHost.ApplyLogonModulePath(start);
        start.Environment["ProgramData"] = programData;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var acl = "";
        var folder = Path.Combine(programData, config.FolderName);
        if (Directory.Exists(folder))
        {
            var icacls = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{folder}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            acl = icacls.StandardOutput.ReadToEnd();
            icacls.WaitForExit();
        }
        return new BlockRun(process.ExitCode, (stdout + "\n" + stderr).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray(), acl);
    }

    private static string Password(BlockRun run) =>
        run.Output.Where(l => l.StartsWith("PW=", StringComparison.Ordinal)).Select(l => l[3..]).SingleOrDefault() ?? "";

    private static readonly TargetSecretConfig Postgres17 = new()
    { FolderName = "PostgreSQL-Superuser-Secret", FileName = "postgresql-17.dpapi", Label = "PostgreSQL superuser" };

    [Fact]
    public void ATargetCreatesA32CharacterSecretOnceAndReusesIt()
    {
        var pd = Path.Combine(_dir, "pd1");
        var first = RunBlock(Postgres17, pd);
        Assert.Equal(0, first.ExitCode);
        var password = Password(first);
        Assert.Equal(32, password.Length);
        Assert.Contains(@"NT AUTHORITY\SYSTEM", first.Acl);
        Assert.Contains(@"BUILTIN\Administrators", first.Acl);
        Assert.DoesNotContain(@"BUILTIN\Users", first.Acl);

        var file = Path.Combine(pd, Postgres17.FolderName, Postgres17.FileName);
        Assert.True(File.Exists(file));
        Assert.DoesNotContain(password, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));

        var second = RunBlock(Postgres17, pd);
        Assert.True(password == Password(second), "Second run exit " + second.ExitCode + ": " + string.Join(" | ", second.Output) + " ACL: " + second.Acl);
    }

    [Fact]
    public void TwoTargetsGetDifferentSecrets() =>
        Assert.NotEqual(Password(RunBlock(Postgres17, Path.Combine(_dir, "ta"))), Password(RunBlock(Postgres17, Path.Combine(_dir, "tb"))));

    [Fact]
    public void AFolderThatAStandardUserCouldHaveCreatedIsRefused()
    {
        var pd = Path.Combine(_dir, "planted");
        var planted = Path.Combine(pd, Postgres17.FolderName);
        Directory.CreateDirectory(planted);
        var grant = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{planted}\" /grant \"BUILTIN\\Users:(OI)(CI)R\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        grant.StandardOutput.ReadToEnd();
        grant.WaitForExit();
        File.WriteAllBytes(Path.Combine(planted, Postgres17.FileName), [1, 2, 3]);

        var run = RunBlock(Postgres17, pd);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains(run.Output, l => l.Contains("Could not create the PostgreSQL superuser secret", StringComparison.Ordinal));
        Assert.Equal("", Password(run));
    }

    [Fact]
    public void ASecretThatCannotBeStoredEndsTheInstallWithoutAPassword()
    {
        var pd = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(pd);
        File.WriteAllText(Path.Combine(pd, Postgres17.FolderName), "a file where the folder must go");

        var run = RunBlock(Postgres17, pd);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal("", Password(run));
    }

    // ---- CutePDF Writer ----------------------------------------------------

    [Fact]
    public void CutePdfWriterDownloadsOverHttpsAndRequiresTheVendorSignature()
    {
        var def = DefinitionFixture.Load("cutepdfwriter.json");
        Assert.Equal("https://www.cutepdf.com/download/CuteWriter.exe", def.DownloadUrl);
        Assert.Equal("https://www.cutepdf.com/download/CuteWriter.exe", def.VersionScraper.Url);
        Assert.True(def.RequireValidSignature);
    }

    [Theory]
    [MemberData(nameof(AllDefinitions))]
    public void NoDefinitionDownloadsOrScrapesOverPlainHttp(string file)
    {
        var def = DefinitionFixture.Load(file);
        foreach (var url in new[] { def.DownloadUrl, def.VersionScraper.Url })
            Assert.False(url?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true, $"{file} uses {url}.");
    }

    // ---- the .NET Desktop runtime -----------------------------------------

    [Theory]
    [InlineData("dotnet8-x64.json", @"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.21")]
    [InlineData("dotnet8-x86.json", @"C:\Program Files (x86)\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.21")]
    [InlineData("Dotnet9x64.json", @"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.21")]
    [InlineData("Dotnet10x64.json", @"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.21")]
    public void TheDesktopRuntimeIsDetectedByItsOwnFrameworkFolder(string file, string path)
    {
        var det = StagingService.BuildDetection(DefinitionFixture.Load(file), null, "8.0.21", tokens: new StageTokens { Version = "8.0.21" });
        Assert.Equal("File", det.Type);
        Assert.Equal(path, det.FilePath);
        Assert.Equal("PresentationFramework.dll", det.FileName);
    }

    [Theory]
    [MemberData(nameof(AllDefinitions))]
    public void NoDefinitionDetectsADesktopRuntimeThroughTheHostResolver(string file)
    {
        var det = DefinitionFixture.Load(file).Detection;
        Assert.DoesNotContain("host\\fxr", det.FilePath ?? "");
        Assert.NotEqual("hostfxr.dll", det.FileName);
    }

    // ---- GIMP --------------------------------------------------------------

    [Theory]
    [InlineData("<a>0.0_LATEST-IS-3.0.8-2</a>", "3.0.8-2")]
    [InlineData("<a>0.0_LATEST-IS-3.0.8-10</a>", "3.0.8-10")]
    [InlineData("<a>0.0_LATEST-IS-3.0.8-1</a>", "3.0.8")]
    [InlineData("<a>0.0_LATEST-IS-3.0.8</a>", "3.0.8")]
    public void GimpTakesTheRevisionOfARebuiltInstallerAsPartOfTheRelease(string listing, string version)
    {
        var config = DefinitionFixture.Load("gimp.json").VersionScraper;
        Assert.Equal(version, HttpRegexScraper.ExtractVersion(listing, config.Pattern, false));
    }

    [Theory]
    [InlineData("3.0.8-2", "gimp-3.0.8-setup-2.exe")]
    [InlineData("3.0.8", "gimp-3.0.8-setup.exe")]
    public void GimpDownloadsTheRebuiltInstaller(string version, string file)
    {
        var def = DefinitionFixture.Load("gimp.json");
        var url = StagingService.ApplyDashedVersion(def.DownloadUrl.Replace("{version}", version), version);
        Assert.Equal("https://download.gimp.org/gimp/v3.0/windows/" + file, url);
    }

    [Theory]
    [InlineData("3.0.8-2")]
    [InlineData("3.0.8")]
    public void GimpDetectsTheVersionTheInstalledProductShows(string version)
    {
        var det = StagingService.BuildDetection(DefinitionFixture.Load("gimp.json"), null, version,
            tokens: new StageTokens { Version = version });
        Assert.Equal("3.0.8", det.ExpectedValue);
        Assert.Equal("GreaterEquals", det.Operator);
    }
}
