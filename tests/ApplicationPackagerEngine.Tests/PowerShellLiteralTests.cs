using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerEngine.Tests.Signing;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Generated PowerShell is checked under Windows PowerShell 5.1: a value with
/// any of its quote characters comes back unchanged from a literal and never
/// adds a statement to the generated script.
/// </summary>
public sealed class PowerShellLiteralTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-pslit-" + Guid.NewGuid().ToString("N")[..8]);

    public PowerShellLiteralTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    public static TheoryData<string> SingleQuotes => new() { "'", "\u2018", "\u2019", "\u201A", "\u201B" };
    public static TheoryData<string> DoubleQuotes => new() { "\"", "\u201C", "\u201D", "\u201E" };

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>Evaluates a PowerShell expression under powershell.exe and returns its string value.</summary>
    private static string Evaluate(string expression)
    {
        var line = SigningCertificateFixture.RunWindowsPowerShell(
            $"$v = {expression}; [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$v))").Last();
        return Encoding.UTF8.GetString(Convert.FromBase64String(line));
    }

    /// <summary>
    /// Parses a script file as Windows PowerShell 5.1 reads it and returns the
    /// parse error count and the number of Write-Output INJECTED commands.
    /// </summary>
    private (int Errors, int Injected) Parse(string script)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(path, script, new UTF8Encoding(true));
        var output = SigningCertificateFixture.RunWindowsPowerShell(
            "$t = $null; $e = $null; " +
            $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({SigningCertificateFixture.PsLiteral(path)}, [ref]$t, [ref]$e); " +
            "$hits = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-Output' -and $n.Extent.Text -match 'INJECTED' }, $true)); " +
            "Write-Output (\"{0} {1}\" -f @($e).Count, $hits.Count)").Last().Split(' ');
        return (int.Parse(output[0]), int.Parse(output[1]));
    }

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void ASingleQuotedLiteral_ReturnsTheOriginalString(string quote)
    {
        var original = $"Acme{quote}; Write-Output INJECTED; {quote}x";
        Assert.Equal(Base64(original), Base64(Evaluate(PowerShellLiteral.SingleQuoted(original))));
    }

    [Theory]
    [MemberData(nameof(DoubleQuotes))]
    public void ADoubleQuotedString_ReturnsTheOriginalString(string quote)
    {
        var original = $"Acme{quote}; $(Write-Output INJECTED) `t {quote}x";
        Assert.Equal(Base64(original), Base64(Evaluate("\"" + PowerShellLiteral.EscapeDoubleQuoted(original) + "\"")));
    }

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void AnApplicationTitle_NeverAddsAStatementToTheConfigMgrScripts(string quote)
    {
        var title = $"Acme{quote}; Write-Output INJECTED; {quote}x";
        var manifest = new StageManifest
        {
            AppName = title, Publisher = $"Pub{quote}lisher", SoftwareVersion = "1.0", DisplayName = title,
            Detection = new StageDetection
            {
                Type = "RegistryKeyValue", RegistryKeyRelative = $@"SOFTWARE\{quote}Key", ValueName = "DisplayVersion", ExpectedValue = "1.0"
            }
        };
        foreach (var script in new[]
                 {
                     MecmTarget.BuildExistingProbeScript(title, "1.0", "P01"),
                     MecmTarget.BuildPackageScript(manifest, title, @"\\server\share\App", "P01", title, 15, 30),
                     MecmTarget.BuildPackageScript(manifest, title, @"\\server\share\App", "P01", title, 15, 30, onExisting: OnExistingBehavior.Overwrite),
                     MecmTarget.BuildDistributionScript(title, "P01", title, null),
                     MecmTarget.BuildTestCollectionDeploymentScript(title, "P01", title, true, null),
                     MecmTarget.BuildContentStepsScript(title, "P01", title, null, title, true, false),
                     SupersedenceScripts.BuildLookupScript("P01", null, "APD-Line:" + title + "|default|x64", "12"),
                     SupersedenceScripts.BuildStepScript("P01", null, "12", "11", [new DeploymentTypePair(new LineDeploymentType("121", title), new LineDeploymentType("111", title))], true),
                     MecmTarget.BuildPackageScript(manifest, title, @"\\server\share\App", "P01", title, 15, 30, identityMarker: "APD-Line:" + title + "|default|x64")
                 })
            Assert.Equal((0, 0), Parse(script));
    }

    [Theory]
    [MemberData(nameof(DoubleQuotes))]
    public void AnApplicationTitle_IsNotExpandedInsideTheConfigMgrMessages(string quote)
    {
        var title = $"Acme{quote} $(Write-Output INJECTED) {quote}x";
        var manifest = new StageManifest
        {
            AppName = title, Publisher = "Vendor", SoftwareVersion = "1.0$(Write-Output INJECTED)",
            Detection = new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Vendor" }
        };
        foreach (var behavior in new[] { OnExistingBehavior.Skip, OnExistingBehavior.Overwrite, OnExistingBehavior.Fail })
            Assert.Equal((0, 0), Parse(MecmTarget.BuildPackageScript(manifest, title, @"\\server\share\App", "P01", "c", 15, 30, onExisting: behavior)));
        Assert.Equal((0, 0), Parse(MecmTarget.BuildExistingProbeScript(title, "1.0", "P01")));
    }

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void TheMsixDetectionScript_KeepsThePackageNameLiteral(string quote)
    {
        var detection = StagingService.BuildMsixScriptDetection(new StageMsixPackage
        {
            Name = $"Acme{quote}; Write-Output INJECTED; {quote}x", Version = "1.0.0.0", ProcessorArchitecture = "x64"
        });
        Assert.Equal((0, 0), Parse(detection.ScriptText!));
    }

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void TheVpnConditionScript_KeepsItsPatternsLiteral(string quote)
    {
        var script = ConditionTemplateService.BuildVpnScriptText([$"Cisco{quote}; Write-Output INJECTED; {quote}"], $"vpn{quote}; Write-Output INJECTED; {quote}");
        Assert.Equal((0, 0), Parse(script));
    }

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void TheIntuneRegistryScriptRule_KeepsTheExpectedValueLiteral(string quote)
    {
        var rules = IntunePublishService.BuildRules(new StageDetection
        {
            Type = "RegistryKeyValue", RegistryKeyRelative = $@"SOFTWARE\Acme{quote}", ValueName = "DisplayVersion",
            Operator = "BeginsWith", ExpectedValue = $"1{quote}; Write-Output INJECTED; {quote}"
        });
        var script = Encoding.UTF8.GetString(Convert.FromBase64String((string)rules[0]!["scriptContent"]!));
        Assert.Equal((0, 0), Parse(script));
    }

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void TheRequirementRules_KeepTemplateValuesLiteral(string quote)
    {
        var template = new ConditionTemplate
        {
            Id = $"id{quote}", GlobalConditionName = $"Name{quote}; Write-Output INJECTED; {quote}", Kind = "Wql",
            RuleType = "CommonValue", DataType = "String", Namespace = "root\\cimv2", Class = "Win32_X", Property = "P",
            Description = $"d{quote}"
        };
        var sb = new StringBuilder();
        RequirementRuleBuilder.AppendRules(sb, [new ResolvedRequirement { Template = template, Value = $"v{quote}; Write-Output INJECTED; {quote}" }], "dt0");
        Assert.Equal((0, 0), Parse(sb.ToString()));
    }

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void WrapperLiterals_KeepFileNamesAndArgumentsLiteral(string quote)
    {
        var name = $"x{quote}; Write-Output INJECTED; #.exe";
        var args = $"/S {quote}; Write-Output INJECTED; {quote}";
        foreach (var script in new[]
                 {
                     WrapperGeneratorService.BuildExeInstallScript(name, args),
                     WrapperGeneratorService.BuildExeInstallScript(name, args, postInstallKillProcesses: ["app"]),
                     WrapperGeneratorService.BuildArpLookupUninstall(name, args, null, $"App{quote}", $"key{quote}", $"C:\\{quote}"),
                     WrapperGeneratorService.BuildArpLookupUninstall(name, args, "InnoSetup", null, null, null)
                 })
            Assert.Equal((0, 0), Parse(script));

        var dir = Path.Combine(_root, "msi-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        new WrapperGeneratorService().GenerateMsiWrappers(dir, $"x{quote}; Write-Output INJECTED; #.msi", installArgs: args);
        Assert.Equal((0, 0), Parse(File.ReadAllText(Path.Combine(dir, "install.ps1"))));
        Assert.Equal((0, 0), Parse(File.ReadAllText(Path.Combine(dir, "uninstall.ps1"))));
    }

    [Theory]
    [MemberData(nameof(SingleQuotes))]
    public void ADetectionLineThatEndsTheHereString_IsRefusedForEveryQuoteCharacter(string quote)
    {
        var manifest = new StageManifest
        {
            AppName = "App", Publisher = "Vendor", SoftwareVersion = "1.0",
            Detection = new StageDetection { Type = "Script", ScriptText = $"exit 0\r\n{quote}@; Write-Output INJECTED\r\n" }
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MecmTarget.BuildPackageScript(manifest, "App", @"\\server\share\App", "P01", "c", 15, 30));
        Assert.Contains("here-string terminator", ex.Message);
    }

    [Fact]
    public void AnInstallerNameWithAnApostrophe_ProducesAParsableScriptThatNamesTheFile()
    {
        var script = WrapperGeneratorService.BuildExeInstallScript("O'Brien Setup.exe", "/S");
        Assert.Equal((0, 0), Parse(script));
        Assert.Contains("Join-Path $PSScriptRoot 'O''Brien Setup.exe'", script);
    }

    [Fact]
    public void InputsWithoutQuoteCharacters_AreUnchanged()
    {
        Assert.Equal("Acme App (x64) 1.0 $x `t", PowerShellLiteral.EscapeSingleQuoted("Acme App (x64) 1.0 $x `t"));
        Assert.Equal(
            "$exePath = Join-Path $PSScriptRoot 'setup.exe'\r\n" +
            "$proc = Start-Process -FilePath $exePath -ArgumentList '/S /D=C:\\App' -Wait -PassThru -NoNewWindow\r\n" +
            "$proc.ExitCode | Out-File (Join-Path $PSScriptRoot 'exitcode.txt') -NoNewline\r\n" +
            "exit $proc.ExitCode",
            WrapperGeneratorService.BuildExeInstallScript("setup.exe", "/S /D=C:\\App"));
    }
}
