using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Twelve Microsoft Build of OpenJDK definitions: features 17, 21 and 25,
/// each as MSI and EXE, per machine and per user. The version comes from the
/// MSI alias redirect, the payload hash from the vendor checksum file beside
/// the download, and the MSI install folder carries the MSI ProductVersion.
/// </summary>
public sealed class MsOpenJdkDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-openjdk-" + Guid.NewGuid().ToString("N"));

    public MsOpenJdkDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    public static TheoryData<string> AllDefinitions()
    {
        var data = new TheoryData<string>();
        foreach (var n in new[] { 17, 21, 25 })
            foreach (var v in new[] { "msi", "msi-user", "exe", "exe-user" })
                data.Add($"ms-openjdk{n}-{v}.json");
        return data;
    }

    private static string RedirectPattern(int feature) =>
        DefinitionFixture.Load($"ms-openjdk{feature}-msi.json").VersionScraper.Pattern["REDIR:".Length..];

    [Theory]
    [InlineData("https://download.visualstudio.microsoft.com/download/pr/f1e5f23f/fe87dea0/microsoft-jdk-21.0.12.1-windows-x64.msi", "21.0.12.1")]
    [InlineData("https://host/microsoft-jdk-21.0.12-windows-x64.msi", "21.0.12")]
    [InlineData("https://host/microsoft-jdk-21.0.12.1.1-windows-x64.msi", "21.0.12.1.1")]
    [InlineData("https://host/microsoft-jdk-21.0.4-windows-x64.msi?sig=abc", "21.0.4")]
    public void TheVersionIsReadFromTheRedirectTarget(string url, string expected)
    {
        Assert.Equal(expected, HttpRegexScraper.ExtractVersion(url, RedirectPattern(21), false));
    }

    [Theory]
    [InlineData("https://www.bing.com/?ref=aka&shorturl=download-jdk/microsoft-jdk-21-windows-x64.exe")]
    [InlineData("https://host/microsoft-jdk-21-windows-x64.msi")]
    [InlineData("https://host/microsoft-jdk-21.0.12.1.1.1-windows-x64.msi")]
    [InlineData("https://host/microsoft-jdk-17.0.20.1-windows-x64.msi")]
    [InlineData("https://host/microsoft-jdk-210.0.1-windows-x64.msi")]
    [InlineData("https://host/microsoft-jdk-21.0.12.1-windows-aarch64.msi")]
    [InlineData("https://host/microsoft-jdk-21.0.12.1-windows-x64.zip")]
    [InlineData("https://host/microsoft-jdk-21.0.12.1-windows-x64.exe")]
    [InlineData("https://host/microsoft-jdk-21.0.12.1-windows-x64.msi.sha256sum.txt")]
    [InlineData("")]
    public void ARedirectToAnythingButAJdk21X64MsiYieldsNoVersion(string url)
    {
        Assert.Null(HttpRegexScraper.ExtractVersion(url, RedirectPattern(21), false));
    }

    [Fact]
    public void TheChecksumLineForTheFileIsReturnedLowerCased()
    {
        Assert.Equal("3a3f7e1a9fd9edd1fcc0545ffbe7dc87787a8417dc1bf303e41c7a697c8490c1",
            StagingService.ParseChecksumFile("3A3F7E1A9FD9EDD1FCC0545FFBE7DC87787A8417DC1BF303E41C7A697C8490C1  microsoft-jdk-21.0.12.1-windows-x64.msi\n",
                "microsoft-jdk-21.0.12.1-windows-x64.msi"));
    }

    [Fact]
    public void TheBinaryModeAsteriskAndCrlfLineEndsAreAccepted()
    {
        Assert.Equal("8d06f74ecb94729c353615c4af279681166f667733d307df92d91f63b0844cdf",
            StagingService.ParseChecksumFile("8d06f74ecb94729c353615c4af279681166f667733d307df92d91f63b0844cdf *microsoft-jdk-21.0.12.1-windows-x64.exe\r\n",
                "microsoft-jdk-21.0.12.1-windows-x64.exe"));
    }

    [Fact]
    public void ABareHashWithNoFileNameMatches()
    {
        Assert.Equal(new string('a', 64), StagingService.ParseChecksumFile(new string('a', 64), "x.msi"));
    }

    [Theory]
    [InlineData("3a3f7e1a9fd9edd1fcc0545ffbe7dc87787a8417dc1bf303e41c7a697c8490c1  microsoft-jdk-21.0.12.1-windows-x64.exe")]
    [InlineData("3a3f7e1a  microsoft-jdk-21.0.12.1-windows-x64.msi")]
    [InlineData("<html><body>Not found</body></html>")]
    [InlineData("")]
    public void NoLineForTheFileOrAMalformedHashYieldsNothing(string content)
    {
        Assert.Null(StagingService.ParseChecksumFile(content, "microsoft-jdk-21.0.12.1-windows-x64.msi"));
    }

    [Theory]
    [MemberData(nameof(AllDefinitions))]
    public void EveryVariantReadsTheMsiAliasAndVerifiesTheChecksumFile(string file)
    {
        var def = DefinitionFixture.Load(file);
        var feature = file["ms-openjdk".Length..][..2];
        var ext = file.Contains("-msi") ? "msi" : "exe";

        Assert.Equal($"https://aka.ms/download-jdk/microsoft-jdk-{feature}-windows-x64.msi", def.VersionScraper.Url);
        Assert.Equal($"https://aka.ms/download-jdk/microsoft-jdk-{{version}}-windows-x64.{ext}", def.DownloadUrl);
        Assert.Equal("{download_url}.sha256sum.txt", def.PayloadSha256Url);
        Assert.Equal(["java", "javaw"], def.RunningProcess);
        Assert.Equal("Microsoft", def.Vendor);
        Assert.Equal(file.EndsWith("-user.json") ? "User" : "Machine", def.InstallContext);
    }

    [Theory]
    [InlineData("ms-openjdk21-msi.json", "Microsoft OpenJDK 21 (MSI)")]
    [InlineData("ms-openjdk21-msi-user.json", "Microsoft OpenJDK 21 (MSI, per user)")]
    [InlineData("ms-openjdk17-exe.json", "Microsoft OpenJDK 17 (EXE)")]
    [InlineData("ms-openjdk25-exe-user.json", "Microsoft OpenJDK 25 (EXE, per user)")]
    public void TheConsoleNameNamesTheFeatureAndTheVariant(string file, string cmName)
    {
        Assert.Equal(cmName, DefinitionFixture.Load(file).CMName);
    }

    [Fact]
    public void ThePerMachineMsiInstallsUnderProgramFilesAndDetectsItsOwnArpEntry()
    {
        var def = DefinitionFixture.Load("ms-openjdk21-msi.json");
        var tokens = new StageTokens { Version = "21.0.12.1", ProductVersion = "21.0.12.1" };

        Assert.True(def.ExpandInstallArgs);
        Assert.Equal(@"ADDLOCAL=FeatureMain,FeatureEnvironment,FeatureJarFileRunWith INSTALLDIR=""%ProgramW6432%\Microsoft\jdk-21.0.12.1-hotspot"" /qn /norestart",
            tokens.Apply(def.InstallArgs));

        var staged = StagingService.BuildDetection(def, "{GUID-1}", "21.0.12.1", "21.0.12.1", tokens: tokens);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID-1}", staged.RegistryKeyRelative);
        Assert.Equal("21.0.12.1", staged.ExpectedValue);
        Assert.True(staged.Is64Bit);
    }

    [Fact]
    public void ThePerUserMsiInstallsUnderTheProfileAndDetectsJavaExe()
    {
        var def = DefinitionFixture.Load("ms-openjdk17-msi-user.json");
        var tokens = new StageTokens { Version = "17.0.20.1", ProductVersion = "17.0.20.1" };

        Assert.StartsWith("ALLUSERS=2 MSIINSTALLPERUSER=1 ADDLOCAL=FeatureMain,FeatureEnvironment,FeatureJarFileRunWith", def.InstallArgs);
        Assert.Contains(@"INSTALLDIR=""%LOCALAPPDATA%\Programs\Microsoft\jdk-17.0.20.1-hotspot""", tokens.Apply(def.InstallArgs));

        var staged = StagingService.BuildDetection(def, "{GUID-1}", "17.0.20.1", tokens: tokens);
        Assert.Equal("File", staged.Type);
        Assert.Equal(@"%LOCALAPPDATA%\Programs\Microsoft\jdk-17.0.20.1-hotspot\bin", staged.FilePath);
        Assert.Equal("java.exe", staged.FileName);
        Assert.Equal("Existence", staged.PropertyType);
    }

    [Fact]
    public void TheMsiWrapperExpandsTheInstallFolderOnTheClient()
    {
        var args = new StageTokens { Version = "21.0.12.1", ProductVersion = "21.0.12.1" }
            .Apply(DefinitionFixture.Load("ms-openjdk21-msi.json").InstallArgs)!;

        new WrapperGeneratorService().GenerateMsiWrappers(_dir, "microsoft-jdk-21.0.12.1-windows-x64.msi", null, args, expandEnvironment: true);

        Assert.Contains($"[Environment]::ExpandEnvironmentVariables('{args}')", File.ReadAllText(Path.Combine(_dir, "install.ps1")));
    }

    [Theory]
    [InlineData("ms-openjdk17-exe.json", "HKLM", "/ALLUSERS", "{65A75ED2-4165-6FAA-277E-7647F7E2F3EA}_is1")]
    [InlineData("ms-openjdk21-exe.json", "HKLM", "/ALLUSERS", "{11F8EC6C-68A4-9099-96F9-E81566D319CC}_is1")]
    [InlineData("ms-openjdk25-exe.json", "HKLM", "/ALLUSERS", "{9DA4BE12-C58B-3DE4-C047-AF46365065FC}_is1")]
    [InlineData("ms-openjdk21-exe-user.json", "HKCU", "/CURRENTUSER", "{11F8EC6C-68A4-9099-96F9-E81566D319CC}_is1")]
    public void TheExeVariantsDetectTheirInnoKeyAgainstTheHeaderDisplayVersion(string file, string hive, string mode, string key)
    {
        var def = DefinitionFixture.Load(file);

        Assert.Equal($"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- {mode} /TASKS=\"FeatureEnvironment,FeatureJarFileRunWith\"", def.InstallArgs);
        Assert.Contains($"$key = '{hive}:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{key}'", def.UninstallCommand);

        var staged = StagingService.BuildDetection(def, null, "21.0.12.1",
            tokens: new StageTokens { Version = "21.0.12.1", HeaderDisplayVersion = "21.0.12.1" });
        Assert.Equal($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{key}", staged.RegistryKeyRelative);
        Assert.Equal(hive == "HKCU" ? "CurrentUser" : "LocalMachine", staged.Hive);
        Assert.Equal("21.0.12.1", staged.ExpectedValue);
        Assert.True(staged.Is64Bit);
    }
}
