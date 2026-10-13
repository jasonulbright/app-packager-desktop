using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>Signed transport, read-back and rule settings in the ConfigMgr script and the Intune rules.</summary>
[Collection(SigningCollection.Name)]
public sealed class SignedTargetScriptTests(SigningCertificateFixture fixture)
{
    private const string Content = @"\\server\share\Apps\Vendor\App\1.0";

    private static StageManifest ScriptManifest() => new()
    {
        AppName = "App",
        Publisher = "Vendor",
        SoftwareVersion = "1.0",
        Detection = new StageDetection { Type = "Script", ScriptText = "Write-Output 'Installed'\r\nexit 0" }
    };

    private static ScriptSigningRecord Record(string status, string? file = @"scripts\detect.ps1", string thumbprint = "ABCD") => new()
    {
        Detection = new DetectionSigningRecord { Status = status, File = file, Thumbprint = thumbprint }
    };

    private static string Build(StageManifest manifest, MecmScriptSigningContext? signing,
        IReadOnlyList<StageRequirement>? requirements = null, StageCommandOverrides? overrides = null) =>
        MecmTarget.BuildPackageScript(manifest, manifest.AppName, Content, "P01", "comment", 15, 30,
            ConditionTemplateService.Defaults(), overrides, null, requirements, null, OnExistingBehavior.Skip, signing);

    [Fact]
    public void ASignedFinalizedDetection_ImportsThroughScriptFile_AndReadsBackBytesAndSigner()
    {
        var script = Build(ScriptManifest(), new MecmScriptSigningContext
        {
            Policy = new SigningPolicy { RequireDetection = true },
            Record = Record("SignedAndVerified")
        });

        Assert.Contains($@"$detectTransport0 = Resolve-ApkgDetectionScriptFile -Path '{Content}\scripts\detect.ps1' -ScriptFile 'scripts\detect.ps1' -ContentLocation '{Content}' -Signed $true", script);
        Assert.Contains("-ScriptLanguage PowerShell -ScriptFile $detectTransport0.Path", script);
        Assert.DoesNotContain("-ScriptText $detectScript0", script);
        Assert.DoesNotContain("$detectScript0 = @'", script);
        Assert.Contains("Test-ApkgStoredDetectionScript -ApplicationName 'App' -DeploymentTypeName $dtCreateName0 -Transport $detectTransport0 -ExpectedThumbprint 'ABCD' -RequireDetection $true", script);
        // The UNC copy: the site server reads -ScriptFile from the console host.
        Assert.Contains("if ($Path -like '\\\\*') {", script);
    }

    [Fact]
    public void AnUnsignedFinalizedDetection_StillImportsTheFileItsBuildWrote()
    {
        var script = Build(ScriptManifest(), new MecmScriptSigningContext { Record = Record("NotRequested") });
        Assert.Contains("-Signed $false", script);
        Assert.Contains("-ScriptFile $detectTransport0.Path", script);
        Assert.Contains("-ExpectedThumbprint '' -RequireDetection $false", script);
    }

    [Fact]
    public void ASignedDetectionWithoutAFinalizedFile_IsRefusedBeforeAnySiteWrite()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(ScriptManifest(), new MecmScriptSigningContext { Record = Record("SignedAndVerified", file: null) }));
        Assert.Equal("The detection script is signed but the manifest names no Detection.ScriptFile; a signed script cannot be imported as script text. Re-run the Stage phase so the finalized script is written to the content folder.", ex.Message);
    }

    [Fact]
    public void ADeploymentTypeDetection_TravelsAsText_AndIsRefusedWhenTheBuildIsSigned()
    {
        var manifest = ScriptManifest();
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "x64", Detection = new StageDetection { Type = "Script", ScriptText = "exit 0" } }
        ];
        var unsigned = Build(manifest, new MecmScriptSigningContext { Record = Record("NotRequested") });
        Assert.Contains("-ScriptText $detectScript0", unsigned);

        Assert.Throws<InvalidOperationException>(() =>
            Build(manifest, new MecmScriptSigningContext { Record = Record("SignedAndVerified") }));
    }

    [Fact]
    public void ResolvedCommands_AreRecheckedWhenDeploymentSigningIsInForce()
    {
        var overrides = new StageCommandOverrides { Install = "powershell.exe -ExecutionPolicy Bypass -File install.ps1" };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(ScriptManifest(), new MecmScriptSigningContext { Policy = new SigningPolicy { RequireDeployment = true } }, overrides: overrides));
        Assert.Contains("DeploymentTypes[1].InstallCommand:0", ex.Message);

        var signedRecord = new ScriptSigningRecord { Deployment = new DeploymentSigningRecord { Status = "SignedAndVerified" } };
        Assert.Throws<InvalidOperationException>(() =>
            Build(ScriptManifest(), new MecmScriptSigningContext { Record = signedRecord }, overrides: overrides));

        // Off and unsigned, the same override is informational and the script builds.
        Assert.Contains("Add-CMScriptDeploymentType", Build(ScriptManifest(), null, overrides: overrides));
    }

    [Fact]
    public void ASignedRequirementScript_TravelsAsItsExactSignedBytesUnderAVersionedName()
    {
        var script = Build(ScriptManifest(), new MecmScriptSigningContext { Policy = fixture.Policy(sr: true) },
            [new StageRequirement { ConditionId = "vpn-connected", Value = true }]);

        var start = script.IndexOf("$dt0_gc0 = Get-ApkgScriptGlobalCondition", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var call = script[start..script.IndexOf("\r\n$dt0_req0", start, StringComparison.Ordinal)];
        var base64 = System.Text.RegularExpressions.Regex.Match(call, "-SignedBase64 '([^']+)'").Groups[1].Value;
        var bytes = Convert.FromBase64String(base64);
        Assert.True(ScriptSignatureVerifier.VerifyBytes(bytes).SignatureIntact);
        // The signed text also travels inline, but only the bytes are imported.
        Assert.Contains(ScriptSignatureVerifier.BeginMarker, call);
        Assert.Contains($"-ContentHash '{ScriptSignatureVerifier.Sha256Hex(bytes)[..8]}'", call);
        Assert.Contains($"-ExpectedThumbprint '{fixture.Thumbprint}'", call);
        Assert.Contains("New-CMGlobalConditionScript -Name $Name -DataType $DataType -ScriptLanguage PowerShell -FilePath $temporary", script);
    }

    [Fact]
    public void RequireRequirementsWithoutSigning_RefusesBeforeAnySiteObjectIsCreated()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(ScriptManifest(), new MecmScriptSigningContext { Policy = fixture.Policy(rr: true) },
                [new StageRequirement { ConditionId = "vpn-connected", Value = true }]));
        Assert.StartsWith("Requirement script signing is required and did not succeed for condition 'APKG - VPN Connected': Failed (", ex.Message);
    }

    [Fact]
    public void TheSameConditionOnTwoDeploymentTypes_ResolvesOneIdentity()
    {
        var manifest = ScriptManifest();
        manifest.Detection = new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\App" };
        manifest.DeploymentTypes =
        [
            new StageDeploymentType { NameSuffix = "a", Requirements = [new StageRequirement { ConditionId = "vpn-connected", Value = true }] },
            new StageDeploymentType { NameSuffix = "b", Requirements = [new StageRequirement { ConditionId = "vpn-connected", Value = false }] }
        ];
        var script = Build(manifest, new MecmScriptSigningContext { Policy = fixture.Policy(sr: true) });
        var hashes = System.Text.RegularExpressions.Regex.Matches(script, "-ContentHash '([0-9a-f]{8})'").Select(m => m.Groups[1].Value).Distinct();
        Assert.Single(hashes);
    }

    [Fact]
    public void TheReadBackHelpers_RunInWindowsPowerShell_AndVerifyStoredBytesAndSigner()
    {
        var dir = fixture.NewDirectory("readback");
        var signedPath = Path.Combine(dir, "signed.ps1");
        File.WriteAllText(signedPath, "Write-Output 'Installed'\r\nexit 0");
        ScriptSigningService.SignFile(signedPath, fixture.Signer, "SHA256", null);
        var bytes = File.ReadAllBytes(signedPath);
        var tampered = (byte[])bytes.Clone();
        tampered[0] ^= 0x20;

        string Body(byte[] b) => "Write-Output 'Installed'&#xD;\n# ENCODEDSCRIPT # Begin Configuration Manager encoded script block #" + Convert.ToBase64String(b) + "# ENCODEDSCRIPT # End Configuration Manager encoded script block #";
        string Sdm(byte[] b) =>
            "<AppMgmtDigest xmlns=\"http://schemas.microsoft.com/SystemCenterConfigurationManager/2009/AppMgmtDigest\">" +
            "<DeploymentType><Title>Other</Title><Installer><DetectAction><Args><Arg Name=\"ScriptBody\">other</Arg></Args></DetectAction></Installer></DeploymentType>" +
            $"<DeploymentType><Title>App's DT</Title><Installer><DetectAction><Args><Arg Name=\"ScriptBody\">{Body(b)}</Arg></Args></DetectAction></Installer></DeploymentType>" +
            "</AppMgmtDigest>";

        var helpers = Path.Combine(dir, "helpers.ps1");
        File.WriteAllText(helpers, MecmScriptSigningPs.ReadBackHelpers() + "\r\n" + MecmScriptSigningPs.DetectionFunctions()
            + "\r\n" + MecmScriptSigningPs.ScriptGlobalConditionFunctions(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "good.xml"), Sdm(bytes));
        File.WriteAllText(Path.Combine(dir, "bad.xml"), Sdm(tampered));

        string Check(string xml, string thumbprint) =>
            $"$body = Get-ApkgSdmScriptBody -SdmPackageXml ([IO.File]::ReadAllText({SigningCertificateFixture.PsLiteral(Path.Combine(dir, xml))})) -DeploymentTypeName \"App's DT\"; " +
            "$stored = ConvertFrom-ApkgEncodedScriptBody -Body $body; " +
            $"'encoded=' + $stored.Encoded; 'same=' + (Compare-ApkgBytes -Left $stored.Bytes -Right ([IO.File]::ReadAllBytes({SigningCertificateFixture.PsLiteral(signedPath)}))); " +
            $"try {{ Test-ApkgStoredScriptSignature -Bytes $stored.Bytes -Context 'probe' -ExpectedThumbprint '{thumbprint}'; 'verified' }} catch {{ 'refused: ' + $_.Exception.Message }}";

        var lines = SigningCertificateFixture.RunWindowsPowerShell(
            $". {SigningCertificateFixture.PsLiteral(helpers)}; " +
            Check("good.xml", fixture.Thumbprint) + "; " +
            Check("good.xml", new string('B', 40)) + "; " +
            Check("bad.xml", fixture.Thumbprint));

        var output = string.Join("\n", lines);
        Assert.Contains("encoded=True", output);
        Assert.Contains("same=True", output);
        Assert.Contains("Signature verified           : probe (" + fixture.Thumbprint + "); chain not trusted on this host: UnknownError", output);
        Assert.Contains("verified", lines);
        Assert.Contains("refused: The stored script for probe is signed by " + fixture.Thumbprint + ", not by the certificate this build signed with (" + new string('B', 40) + ").", lines);
        Assert.Contains(lines, l => l.StartsWith("refused: The signature on the stored script for probe did not verify after read-back: HashMismatch", StringComparison.Ordinal));
        Assert.Contains("same=False", output);
    }

    [Fact]
    public void TheDetectionFile_IsVerifiedAgainRightBeforeItIsSent()
    {
        var signed = Build(ScriptManifest(), new MecmScriptSigningContext
        {
            Policy = new SigningPolicy { RequireDetection = true },
            Record = Record("SignedAndVerified")
        });
        Assert.Contains("-Signed $true -VerifySignature $true -ExpectedThumbprint 'ABCD'", signed);

        var dir = fixture.NewDirectory("presend");
        var intact = Path.Combine(dir, "intact.ps1");
        File.WriteAllText(intact, "Write-Output 'Installed'\r\nexit 0");
        ScriptSigningService.SignFile(intact, fixture.Signer, "SHA256", null);
        var changed = Path.Combine(dir, "changed.ps1");
        var bytes = File.ReadAllBytes(intact);
        bytes[0] ^= 0x20;
        File.WriteAllBytes(changed, bytes);
        var unsigned = Path.Combine(dir, "unsigned.ps1");
        File.WriteAllText(unsigned, "Write-Output 'Installed'\r\nexit 0");

        var helpers = Path.Combine(dir, "helpers.ps1");
        File.WriteAllText(helpers, MecmScriptSigningPs.ReadBackHelpers() + "\r\n" + MecmScriptSigningPs.DetectionFunctions(), new UTF8Encoding(true));
        string Resolve(string label, string path, bool verify, string thumbprint) =>
            $"try {{ $t = Resolve-ApkgDetectionScriptFile -Path {SigningCertificateFixture.PsLiteral(path)} -ScriptFile 'scripts\\detect.ps1' -ContentLocation {SigningCertificateFixture.PsLiteral(dir)} " +
            $"-Signed ${(verify ? "true" : "false")} -VerifySignature ${(verify ? "true" : "false")} -ExpectedThumbprint '{thumbprint}'; '{label}: sent' }} catch {{ '{label}: ' + $_.Exception.Message }}";

        var lines = SigningCertificateFixture.RunWindowsPowerShell(
            $". {SigningCertificateFixture.PsLiteral(helpers)}; " +
            Resolve("intact", intact, true, fixture.Thumbprint) + "; " +
            Resolve("changed", changed, true, fixture.Thumbprint) + "; " +
            Resolve("other signer", intact, true, new string('B', 40)) + "; " +
            Resolve("unsigned required", unsigned, true, "") + "; " +
            Resolve("unsigned not required", unsigned, false, ""));

        Assert.Contains("intact: sent", lines);
        Assert.Contains(lines, l => l.StartsWith("changed: The detection script 'scripts\\detect.ps1' carries no intact signature from the build's certificate and was not sent:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("other signer: The detection script 'scripts\\detect.ps1' carries no intact signature from the build's certificate and was not sent:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("unsigned required: The detection script 'scripts\\detect.ps1' carries no intact signature", StringComparison.Ordinal));
        Assert.Contains("unsigned not required: sent", lines);
    }

    [Fact]
    public void TheReadBack_FindsAScriptGlobalConditionBodyAndComparesTextWithCase()
    {
        var dir = fixture.NewDirectory("condition-body");
        var helpers = Path.Combine(dir, "helpers.ps1");
        File.WriteAllText(helpers, MecmScriptSigningPs.ReadBackHelpers(), new UTF8Encoding(true));
        var xml = Path.Combine(dir, "condition.xml");
        File.WriteAllText(xml, "<DesiredConfigurationDigest xmlns=\"http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/07/10/DesiredConfiguration\">" +
            "<GlobalSettings><Settings><RootComplexSetting><SimpleSetting><ScriptDiscoverySource><DiscoveryScriptBody ScriptType=\"PowerShell\">Write-Output 'Connected'</DiscoveryScriptBody></ScriptDiscoverySource></SimpleSetting></RootComplexSetting></Settings></GlobalSettings></DesiredConfigurationDigest>");

        var lines = SigningCertificateFixture.RunWindowsPowerShell(
            $". {SigningCertificateFixture.PsLiteral(helpers)}; " +
            $"'body=' + (Get-ApkgSdmScriptBody -SdmPackageXml ([IO.File]::ReadAllText({SigningCertificateFixture.PsLiteral(xml)})) -DeploymentTypeName '')");

        Assert.Contains("body=Write-Output 'Connected'", lines);
        Assert.Contains("-cne (ConvertTo-ApkgComparableText $Transport.Text)", MecmScriptSigningPs.DetectionFunctions());
        Assert.Contains("-cne (ConvertTo-ApkgComparableText $ScriptText)", MecmScriptSigningPs.ScriptGlobalConditionFunctions());
        Assert.Contains("-ceq (ConvertTo-ApkgComparableText $ScriptText)", MecmScriptSigningPs.ScriptGlobalConditionFunctions());
    }

    [Fact]
    public void Intune_WithoutAContext_KeepsTheUnenforcedTextRule()
    {
        var rule = IntunePublishService.BuildRules(ScriptManifest().Detection)[0]!;
        Assert.False((bool)rule["enforceSignatureCheck"]!);
        Assert.False((bool)rule["runAs32Bit"]!);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("Write-Output 'Installed'\r\nexit 0")), (string?)rule["scriptContent"]);
    }

    [Fact]
    public void Intune_ASignedDetection_PublishesTheFileBytesWithTheCheckEnforced()
    {
        var dir = fixture.NewDirectory("intune");
        Directory.CreateDirectory(Path.Combine(dir, "scripts"));
        var file = Path.Combine(dir, @"scripts\detect.ps1");
        File.WriteAllText(file, "Write-Output 'Installed caf\u00e9'\r\nexit 0", new UnicodeEncoding(false, true));
        ScriptSigningService.SignFile(file, fixture.Signer, "SHA256", null);

        var record = Record("SignedAndVerified", thumbprint: fixture.Thumbprint);
        record.Detection.Sha256 = ScriptSignatureVerifier.FileSha256Hex(file);
        var context = IntuneScriptRuleContext.From(record, new SigningPolicy(), dir, "x86");
        var rule = IntunePublishService.BuildRules(ScriptManifest().Detection, context)[0]!;

        Assert.True((bool)rule["enforceSignatureCheck"]!);
        Assert.True((bool)rule["runAs32Bit"]!);
        var published = Convert.FromBase64String((string)rule["scriptContent"]!);
        Assert.Equal(File.ReadAllBytes(file), published);
        Assert.True(ScriptSignatureVerifier.VerifyBytes(published).SignatureIntact);
    }

    [Fact]
    public void Intune_RequireDetectionWithoutASignature_RefusesThePublish()
    {
        var context = new IntuneScriptRuleContext { DetectionStatus = "NotRequested", RequireDetection = true };
        var ex = Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(ScriptManifest().Detection, context));
        Assert.Equal("Valid detection script signatures are required, but the staged detection reports status 'NotRequested'; re-stage with signing enabled before publishing to Intune.", ex.Message);

        var none = new IntuneScriptRuleContext { RequireDetection = true };
        Assert.Contains("status 'none'", Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(ScriptManifest().Detection, none)).Message);
    }

    [Fact]
    public void Intune_ANamedButMissingDetectionFile_IsFatal()
    {
        var context = new IntuneScriptRuleContext { DetectionStatus = "NotRequested", DetectionScriptFile = Path.Combine(fixture.Root, "missing.ps1") };
        var ex = Assert.Throws<InvalidOperationException>(() => IntunePublishService.BuildRules(ScriptManifest().Detection, context));
        Assert.StartsWith("The manifest names detection script file", ex.Message);
    }

    [Fact]
    public void Intune_AConvertedRegistryScriptRule_CarriesTheSameSettings()
    {
        var detection = new StageDetection
        {
            Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\App", ValueName = "DisplayVersion",
            ExpectedValue = "1.", Operator = "BeginsWith"
        };
        var rule = (JsonObject)IntunePublishService.BuildRules(detection,
            new IntuneScriptRuleContext { DetectionStatus = "NotApplicable", ScriptHost = "x86" })[0]!;
        Assert.False((bool)rule["enforceSignatureCheck"]!);
        Assert.True((bool)rule["runAs32Bit"]!);
    }
}
