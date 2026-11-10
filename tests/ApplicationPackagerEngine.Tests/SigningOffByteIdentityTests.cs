using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// With every signing switch off, the generated ConfigMgr script and the
/// staged wrappers match the recorded unsigned output byte for byte. Hashes
/// are SHA-256 over the UTF-8 bytes of each output. A Script detection and a
/// Script-kind global condition carry a read-back of what the site stored,
/// signed or not; for those two shapes every line of the recorded fixture is
/// present in order, and the remaining lines are the read-back.
/// </summary>
public sealed class SigningOffByteIdentityTests
{
    private const string Content = @"\\server\share\Apps\Vendor\App\1.0";

    private static StageManifest Registry() => new()
    {
        AppName = "App",
        Publisher = "Vendor",
        SoftwareVersion = "1.0",
        Detection = new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.0"
        }
    };

    private static StageManifest FileVersionUserInstall()
    {
        var m = Registry();
        m.InstallContext = "User";
        m.DisplayName = "App (x64)";
        m.Icon = "app-icon.png";
        m.Detection = new StageDetection
        {
            Type = "File",
            FilePath = @"%LOCALAPPDATA%\Programs\App",
            FileName = "app.exe",
            PropertyType = "Version",
            Operator = "GreaterEquals",
            ExpectedValue = "1.0",
            Is64Bit = true
        };
        return m;
    }

    private static StageManifest CompoundMultiDt()
    {
        var m = Registry();
        m.Detection = new StageDetection
        {
            Type = "Compound",
            Connector = "Or",
            Clauses =
            [
                new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Vendor\App" },
                new StageDetection { Type = "File", FilePath = @"C:\Program Files\App", FileName = "app.exe", PropertyType = "Existence" }
            ]
        };
        m.DeploymentTypes =
        [
            new StageDeploymentType
            {
                NameSuffix = "ARM64", ContentSubpath = "arm64",
                Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "ARM64" }]
            },
            new StageDeploymentType { NameSuffix = "x64", ContentSubpath = "x64" }
        ];
        return m;
    }

    private static StageManifest ScriptDetection()
    {
        var m = Registry();
        m.Detection = new StageDetection
        {
            Type = "Script",
            ScriptText = "if (Test-Path 'C:\\Program Files\\App\\app.exe') { Write-Output 'Installed' }\r\nexit 0"
        };
        return m;
    }

    private static string Build(StageManifest manifest, IReadOnlyList<StageRequirement>? requirements = null,
        OnExistingBehavior onExisting = OnExistingBehavior.Skip, string? provider = null,
        MecmScriptSigningContext? signing = null) =>
        MecmTarget.BuildPackageScript(manifest, manifest.AppName, Content, "P01", "comment",
            15, 30, ConditionTemplateService.Defaults(), null, null, requirements, provider, onExisting, signing);

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The recorded hashes and fixtures cover the script without these arguments, which every
    /// script deployment type carries when the manifest names no post-execution behavior.
    /// </summary>
    private const string DefaultReboot = " -RebootBehavior BasedOnExitCode -UserInteractionMode Hidden";

    /// <summary>The two lines that re-read the application's CI_ID after the revision cleanup, which the recorded hashes also exclude.</summary>
    private static readonly System.Text.RegularExpressions.Regex FinalCiIdLines = new(
        @"^\$finalApp = @\(Get-CMApplication [^\r\n]*\r?\n^if \(\$finalApp\) \{ Write-Host ""Final CI_ID: [^\r\n]*\r?\n",
        System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// The script with the default reboot argument and the final CI_ID re-read removed, after
    /// checking the argument appears once per deployment type and the re-read once.
    /// </summary>
    private static string WithoutDefaultReboot(string script, int deploymentTypes = 1)
    {
        Assert.Equal(deploymentTypes, script.Split(DefaultReboot).Length - 1);
        Assert.Single(FinalCiIdLines.Matches(script));
        return FinalCiIdLines.Replace(script.Replace(DefaultReboot, ""), "");
    }

    private static readonly MecmScriptSigningContext AllOff = new()
    {
        Policy = new SigningPolicy(),
        Record = new ScriptSigningRecord { PolicyDigest = new SigningPolicy().PolicyDigest }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistryDetection_MecmScriptIsUnchanged(bool withContext) =>
        Assert.Equal("C47002A7A1B9B0C24BE38CF5F6F98585AF56AC3162B1A3CFFDE6ADBB5DB0183E",
            Sha(WithoutDefaultReboot(Build(Registry(), signing: withContext ? AllOff : null))));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileDetectionWithWqlAndBuiltInRequirements_MecmScriptIsUnchanged(bool withContext) =>
        Assert.Equal("E5A5528FAF12C58F917C90F0E9348C0A8D1858D3EFD616185B005B011EB1B033",
            Sha(WithoutDefaultReboot(Build(FileVersionUserInstall(),
                [new StageRequirement { ConditionId = "cpu-arch", Value = "x64" },
                 new StageRequirement { ConditionId = "os-language", Cultures = ["de-DE"] }],
                OnExistingBehavior.Overwrite, "cm01.contoso.com", withContext ? AllOff : null))));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompoundMultiDeploymentType_MecmScriptIsUnchanged(bool withContext) =>
        Assert.Equal("AC09A1E6662C414412A19F39F97142FEE1E49FD0646C03F191D9B87B3558BB8B",
            Sha(WithoutDefaultReboot(Build(CompoundMultiDt(), signing: withContext ? AllOff : null), deploymentTypes: 2)));

    /// <summary>A checkout can rewrite the fixture's line endings; the generated script always uses CRLF.</summary>
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).ReplaceLineEndings("\r\n");

    private static void AssertKeepsEveryLineInOrder(string fixture, string current, out List<string> added)
    {
        var before = Fixture(fixture).Split("\r\n");
        var after = current.Split("\r\n");
        added = [];
        var j = 0;
        foreach (var line in after)
        {
            if (j < before.Length && line == before[j]) j++;
            else added.Add(line);
        }
        Assert.Equal(before.Length, j);
    }

    [Fact]
    public void UnsignedScriptDetection_KeepsTodaysLinesAndAddsOnlyTheReadBack()
    {
        AssertKeepsEveryLineInOrder("mecm-unsigned-script-detection.ps1.bin", WithoutDefaultReboot(Build(ScriptDetection())), out var added);

        Assert.Contains("$detectTransport0 = [pscustomobject]@{ Transport = 'Text'; Path = ''; TempPath = ''; Text = $detectScript0; Bytes = [System.Text.Encoding]::UTF8.GetBytes($detectScript0); Signed = $false }", added);
        Assert.Contains("Test-ApkgStoredDetectionScript -ApplicationName 'App' -DeploymentTypeName $dtCreateName0 -Transport $detectTransport0 -ExpectedThumbprint '' -RequireDetection $false", added);
        Assert.DoesNotContain(added, l => l.Contains("Add-CMScriptDeploymentType"));
        Assert.DoesNotContain(added, l => l.Contains("ExecutionPolicy"));
    }

    [Fact]
    public void UnsignedScriptCondition_KeepsTodaysLinesAndResolvesThroughTheVersionedResolver()
    {
        var current = WithoutDefaultReboot(Build(Registry(), [new StageRequirement { ConditionId = "vpn-connected", Value = true }]));
        var before = Fixture("mecm-unsigned-vpn-condition.ps1.bin");

        // The body travels inline with its content hash.
        var template = ConditionTemplateService.Defaults().Conditions.Single(c => c.Id == "vpn-connected");
        var text = ConditionTemplateService.ResolveScriptText(template).Replace("'", "''");
        var description = template.Description!.Replace("'", "''");
        var hash8 = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(
            ConditionTemplateService.ResolveScriptText(template)))).ToLowerInvariant()[..8];
        var oldCall = $"$dt0_gc0 = Get-ApkgGlobalCondition -Name 'APKG - VPN Connected' -PlatformType 0 -Kind 'Script' -DataType 'Boolean' -Namespace '' -Class '' -Property '' -ScriptText '{text}' -Description '{description}'";
        var newCall = $"$dt0_gc0 = Get-ApkgScriptGlobalCondition -Name 'APKG - VPN Connected' -PlatformType 0 -DataType 'Boolean' -Description '{description}' -ScriptText '{text}' -SignedBase64 '' -ContentHash '{hash8}' -ExpectedThumbprint ''";
        Assert.Contains(oldCall, before);
        Assert.Contains(newCall, current);

        AssertKeepsEveryLineInOrder("mecm-unsigned-vpn-condition.ps1.bin",
            current.Replace(newCall, oldCall), out var added);
        Assert.Contains(added, l => l.StartsWith("function Get-ApkgScriptGlobalCondition", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsignedWrappers_AreTheHistoricalLauncher()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apkg-wrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            new WrapperGeneratorService().GenerateMsiWrappers(dir, "setup.msi");
            Assert.Equal(
                "@echo off\r\n" +
                "powershell.exe -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0install.ps1\"\r\n" +
                "set \"PSCODE=%ERRORLEVEL%\"\r\n" +
                "del /q \"%~dp0exitcode.txt\" >nul 2>&1\r\n" +
                "exit /b %PSCODE%",
                File.ReadAllText(Path.Combine(dir, "install.bat")));
            Assert.Equal(
                DeploymentLauncher.Build("uninstall.ps1", false).BatBody,
                File.ReadAllText(Path.Combine(dir, "uninstall.bat")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
