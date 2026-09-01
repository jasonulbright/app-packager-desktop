using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class ScriptAndCompoundDetectionTests
{
    private static AppDefinition WithDetection(DetectionConfig detection) =>
        new()
        {
            Application = "Test App",
            CMName = "Test App",
            InstallerType = "EXE",
            Detection = detection
        };

    private static StageManifest ManifestWith(StageDetection detection) =>
        new()
        {
            AppName = "Test App",
            Publisher = "Test Vendor",
            SoftwareVersion = "1.2.3",
            InstallerFile = "setup.exe",
            InstallerType = "EXE",
            Detection = detection
        };

    private static string Script(StageDetection detection) =>
        MecmTarget.BuildPackageScript(
            ManifestWith(detection), "Test App", @"\\server\share\Test App\1.2.3",
            "AB1", "comment", 15, 30);

    private static StageDetection RegistryClause(string key, bool is64Bit) =>
        new()
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = key,
            ValueName = "DisplayVersion",
            PropertyType = "Version",
            Operator = "GreaterEquals",
            ExpectedValue = "1.2.3",
            Is64Bit = is64Bit
        };

    // ── Script detection ────────────────────────────────────────────

    [Fact]
    public void ScriptDetection_SubstitutesTheVersionPlaceholder()
    {
        var def = WithDetection(new DetectionConfig
        {
            Type = "Script",
            ScriptText = "$wanted = [version]'{version}'\nexit 0"
        });

        var staged = StagingService.BuildDetection(def, productCode: null, version: "4.0.3.2");

        Assert.Equal("Script", staged.Type);
        Assert.Contains("[version]'4.0.3.2'", staged.ScriptText);
        Assert.DoesNotContain("{version}", staged.ScriptText);
    }

    [Fact]
    public void ScriptDetection_SubstitutesEveryOccurrence_AndTheProductCode()
    {
        var def = WithDetection(new DetectionConfig
        {
            Type = "Script",
            ScriptText = "{version} and {version} under {ProductCode}"
        });

        var staged = StagingService.BuildDetection(def, "{1234-5678}", "9.9");

        Assert.Equal("9.9 and 9.9 under {1234-5678}", staged.ScriptText);
    }

    [Fact]
    public void ScriptDetection_DefaultsToPowerShell()
    {
        var def = WithDetection(new DetectionConfig { Type = "Script", ScriptText = "exit 0" });

        Assert.Equal("PowerShell", StagingService.BuildDetection(def, null, "1.0").ScriptLanguage);
    }

    [Fact]
    public void ScriptDetection_KeepsAnExplicitLanguage()
    {
        var def = WithDetection(new DetectionConfig
        {
            Type = "Script",
            ScriptLanguage = "VBScript",
            ScriptText = "WScript.Quit 0"
        });

        Assert.Equal("VBScript", StagingService.BuildDetection(def, null, "1.0").ScriptLanguage);
    }

    [Fact]
    public void ScriptDetection_WithoutABody_Throws()
    {
        var def = WithDetection(new DetectionConfig { Type = "Script" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => StagingService.BuildDetection(def, null, "1.0"));
        Assert.Contains("ScriptText", ex.Message);
    }

    [Fact]
    public void ScriptDetection_ReachesTheDeploymentTypeAsScriptTextNotAClause()
    {
        var script = Script(new StageDetection
        {
            Type = "Script",
            ScriptLanguage = "PowerShell",
            ScriptText = "Write-Output 'Installed'\nexit 0"
        });

        Assert.Contains("$detectScript0 = @'", script);
        Assert.Contains("Write-Output 'Installed'", script);
        Assert.Contains("-ScriptLanguage PowerShell -ScriptText $detectScript0", script);
        Assert.DoesNotContain("-AddDetectionClause", script);
    }

    // ── Compound detection ──────────────────────────────────────────

    [Fact]
    public void CompoundDetection_ResolvesEachClauseWithVersionSubstitution()
    {
        var def = WithDetection(new DetectionConfig
        {
            Type = "Compound",
            Connector = "Or",
            Clauses =
            [
                new() { Type = "File", FilePath = @"C:\App\{version}", FileName = "app.exe", PropertyType = "Existence", Is64Bit = true },
                new() { Type = "File", FilePath = @"C:\App x86\{version}", FileName = "app.exe", PropertyType = "Existence", Is64Bit = false }
            ]
        });

        var staged = StagingService.BuildDetection(def, null, "3.1");

        Assert.Equal("Compound", staged.Type);
        Assert.Equal("Or", staged.Connector);
        Assert.Equal(2, staged.Clauses!.Length);
        Assert.Equal(@"C:\App\3.1", staged.Clauses[0].FilePath);
        Assert.Equal(@"C:\App x86\3.1", staged.Clauses[1].FilePath);
        Assert.True(staged.Clauses[0].Is64Bit);
        Assert.False(staged.Clauses[1].Is64Bit);
    }

    [Fact]
    public void CompoundDetection_DefaultsToAnAndConnector()
    {
        var def = WithDetection(new DetectionConfig
        {
            Type = "Compound",
            Clauses =
            [
                new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\A" },
                new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\B" }
            ]
        });

        Assert.Equal("And", StagingService.BuildDetection(def, null, "1.0").Connector);
    }

    [Fact]
    public void CompoundDetection_WithFewerThanTwoClauses_Throws()
    {
        var def = WithDetection(new DetectionConfig
        {
            Type = "Compound",
            Clauses = [new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\A" }]
        });

        Assert.Throws<InvalidOperationException>(() => StagingService.BuildDetection(def, null, "1.0"));
    }

    [Fact]
    public void CompoundDetection_RefusesANestedCompoundClause()
    {
        var def = WithDetection(new DetectionConfig
        {
            Type = "Compound",
            Clauses =
            [
                new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\A" },
                new() { Type = "Compound", Clauses = [] }
            ]
        });

        var ex = Assert.Throws<InvalidOperationException>(() => StagingService.BuildDetection(def, null, "1.0"));
        Assert.Contains("single registry or file clause", ex.Message);
    }

    [Fact]
    public void CompoundDetection_RefusesAnUnknownConnector()
    {
        var def = WithDetection(new DetectionConfig
        {
            Type = "Compound",
            Connector = "Xor",
            Clauses =
            [
                new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\A" },
                new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\B" }
            ]
        });

        Assert.Throws<InvalidOperationException>(() => StagingService.BuildDetection(def, null, "1.0"));
    }

    [Theory]
    [InlineData(new[] { 1, 1, 2 })]   // three runs: not expressible
    [InlineData(new[] { 0, 4 })]      // an empty run
    [InlineData(new[] { 1, 2 })]      // does not sum to the clause count
    public void CompoundDetection_RefusesMalformedGroupSizes(int[] groupSizes)
    {
        var def = WithDetection(new DetectionConfig
        {
            Type = "Compound",
            Connector = "Or",
            GroupSizes = groupSizes,
            Clauses =
            [
                new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\A" },
                new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\B" },
                new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\C" },
                new() { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\D" }
            ]
        });

        var ex = Assert.Throws<InvalidOperationException>(() => StagingService.BuildDetection(def, null, "1.0"));
        Assert.Contains("GroupSizes", ex.Message);
    }

    // ── Compound clause script generation ───────────────────────────

    [Fact]
    public void SingleClause_StillBindsThroughTheClausesArray()
    {
        var script = Script(RegistryClause(@"SOFTWARE\Vendor\App", is64Bit: true));

        Assert.Contains("$clauses = @($clause)", script);
        Assert.Contains("-AddDetectionClause $clauses", script);
        Assert.DoesNotContain("-DetectionClauseConnector", script);
        Assert.DoesNotContain("-GroupDetectionClauses", script);
    }

    [Fact]
    public void CompoundOr_NumbersEachClause_AndOrsEveryClauseAfterTheFirst()
    {
        var key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NVDA";
        var script = Script(new StageDetection
        {
            Type = "Compound",
            Connector = "Or",
            Clauses = [RegistryClause(key, false), RegistryClause(key, true)]
        });

        Assert.Contains("$clause0 = $clause", script);
        Assert.Contains("$clause1 = $clause", script);
        Assert.Contains("$clauses = @($clause0, $clause1)", script);
        Assert.Contains(
            "-DetectionClauseConnector @(@{LogicalName=$clause1.Setting.LogicalName;Connector='OR'})",
            script);
        Assert.DoesNotContain("-GroupDetectionClauses", script);
    }

    [Fact]
    public void CompoundOr_EmitsBothRegistryViews()
    {
        var key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NVDA";
        var script = Script(new StageDetection
        {
            Type = "Compound",
            Connector = "Or",
            Clauses = [RegistryClause(key, false), RegistryClause(key, true)]
        });

        // The 32-bit view clause carries no -Is64Bit; the 64-bit view does.
        var lines = script.Split("\r\n").Where(l => l.StartsWith("$clause = New-CMDetectionClauseRegistryKeyValue")).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("-Is64Bit", lines[0]);
        Assert.EndsWith("-Is64Bit", lines[1]);
    }

    [Fact]
    public void CompoundAnd_NeedsNoConnector_BecauseAndIsTheCmdletDefault()
    {
        var script = Script(new StageDetection
        {
            Type = "Compound",
            Connector = "And",
            Clauses =
            [
                new() { Type = "File", FilePath = @"C:\A", FileName = "a.exe", PropertyType = "Existence", Is64Bit = false },
                new() { Type = "File", FilePath = @"C:\B", FileName = "b.exe", PropertyType = "Existence", Is64Bit = true }
            ]
        });

        Assert.Contains("$clauses = @($clause0, $clause1)", script);
        Assert.DoesNotContain("-DetectionClauseConnector", script);
    }

    [Fact]
    public void GroupSizes_GroupOnlyTheSecondRun_AndOrItOntoTheFirst()
    {
        // (x86-N AND x64-N) OR (x86-N+1 AND x64-N+1)
        var script = Script(new StageDetection
        {
            Type = "Compound",
            Connector = "Or",
            GroupSizes = [2, 2],
            Clauses =
            [
                new() { Type = "File", FilePath = @"C:\fxr\8.0.1", FileName = "hostfxr.dll", PropertyType = "Existence", Is64Bit = false },
                new() { Type = "File", FilePath = @"C:\fxr\8.0.1", FileName = "hostfxr.dll", PropertyType = "Existence", Is64Bit = true },
                new() { Type = "File", FilePath = @"C:\fxr\8.0.2", FileName = "hostfxr.dll", PropertyType = "Existence", Is64Bit = false },
                new() { Type = "File", FilePath = @"C:\fxr\8.0.2", FileName = "hostfxr.dll", PropertyType = "Existence", Is64Bit = true }
            ]
        });

        Assert.Contains("$clauses = @($clause0, $clause1, $clause2, $clause3)", script);
        Assert.Contains(
            "-GroupDetectionClauses @($clause2.Setting.LogicalName, $clause3.Setting.LogicalName)",
            script);
        Assert.Contains(
            "-DetectionClauseConnector @(@{LogicalName=$clause2.Setting.LogicalName;Connector='OR'})",
            script);
    }

    [Fact]
    public void GroupSizes_TakePrecedenceOverThePlainOrConnector()
    {
        var det = new StageDetection
        {
            Type = "Compound",
            Connector = "Or",
            GroupSizes = [1, 2],
            Clauses =
            [
                new() { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\A" },
                new() { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\B" },
                new() { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\C" }
            ]
        };

        var args = MecmTarget.BuildDetectionClauseConnectorArgs(det);

        // One OR, attached to the second run's first clause — not one per clause.
        Assert.Contains("-GroupDetectionClauses @($clause1.Setting.LogicalName, $clause2.Setting.LogicalName)", args);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(args, "Connector='OR'"));
    }

    [Fact]
    public void MalformedGroupSizes_AreRefusedAtScriptGenerationToo()
    {
        var det = new StageDetection
        {
            Type = "Compound",
            GroupSizes = [1, 1],
            Clauses =
            [
                new() { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\A" },
                new() { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\B" },
                new() { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\C" }
            ]
        };

        Assert.Throws<InvalidOperationException>(() => MecmTarget.BuildDetectionClauseConnectorArgs(det));
    }

    // ── Restored definitions ────────────────────────────────────────

    [Theory]
    [InlineData("nomachine.json")]
    [InlineData("nvda.json")]
    public void BothRegistryViewDefinitions_AreCompoundOrOverTwoViews(string fileName)
    {
        var det = DefinitionFixture.Load(fileName).Detection;

        Assert.Equal("Compound", det.Type);
        Assert.Equal("Or", det.Connector);
        Assert.Equal(2, det.Clauses!.Length);
        Assert.Equal([true, false], det.Clauses.Select(c => c.Is64Bit).OrderDescending());
        Assert.Single(det.Clauses.Select(c => c.RegistryKeyTemplate).Distinct());
    }

    [Fact]
    public void CodeMeterRuntime_IsCompoundOrOverBothProgramFilesPaths()
    {
        var det = DefinitionFixture.Load("codemeterruntime.json").Detection;

        Assert.Equal("Compound", det.Type);
        Assert.Equal("Or", det.Connector);
        Assert.Equal(2, det.Clauses!.Length);
        Assert.All(det.Clauses, c =>
        {
            Assert.Equal("File", c.Type);
            Assert.Equal("CodeMeter.exe", c.FileName);
        });
        Assert.Contains(det.Clauses, c => c.FilePath == @"C:\Program Files\CodeMeter\Runtime\bin");
        Assert.Contains(det.Clauses, c => c.FilePath == @"C:\Program Files (x86)\CodeMeter\Runtime\bin");
    }

    [Theory]
    [InlineData("ocenaudio.json")]
    [InlineData("openshot.json")]
    [InlineData("picpick.json")]
    [InlineData("pidgin.json")]
    [InlineData("anyburn.json")]
    [InlineData("bulkrenameutility.json")]
    [InlineData("teracopy.json")]
    public void ArpScanDefinitions_CarryAPowerShellScriptCoveringBothRegistryViews(string fileName)
    {
        var det = DefinitionFixture.Load(fileName).Detection;

        Assert.Equal("Script", det.Type);
        Assert.Equal("PowerShell", det.ScriptLanguage);
        Assert.Contains("{version}", det.ScriptText);
        Assert.Contains(@"CurrentVersion\Uninstall", det.ScriptText);
        Assert.Contains(@"WOW6432Node", det.ScriptText);
    }

    [Theory]
    [InlineData("ocenaudio.json")]
    [InlineData("openshot.json")]
    [InlineData("teracopy.json")]
    public void ArpScanScripts_StageIntoAnEmbeddableDeploymentTypeScript(string fileName)
    {
        var def = DefinitionFixture.Load(fileName);
        var staged = StagingService.BuildDetection(def, null, "7.7.7");

        var script = Script(staged);

        Assert.Contains("$detectScript0 = @'", script);
        Assert.Contains("[version]'7.7.7'", script);
        Assert.Contains("-ScriptLanguage PowerShell -ScriptText $detectScript0", script);
    }
}
