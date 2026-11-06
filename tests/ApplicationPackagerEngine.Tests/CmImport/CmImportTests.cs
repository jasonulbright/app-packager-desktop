using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.CmImport;

namespace ApplicationPackagerEngine.Tests.CmImport;

/// <summary>
/// The ConfigMgr to Intune conversion (PowerShell commits 4c44367 and bec99a6): the stored application
/// document, the detection and requirement rules, the variant selection, the plan and the staged output.
/// The fixtures are documents captured from a lab site.
/// </summary>
public sealed class CmImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-cmimport-" + Guid.NewGuid().ToString("N"));

    public CmImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"The test folder '{_dir}' was not deleted: {ex.Message}");
        }
    }

    private static string FixtureXml(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CmImport", name + ".app.xml"), Encoding.UTF8);

    private static CmApplication Read(string name) => CmApplicationDigestReader.Read(FixtureXml(name));

    private static string Decode(JsonObject rule) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(rule["scriptContent"]!.GetValue<string>()));

    private static DetectionLeaf Leaf(string source = "File", string property = "Exists", string op = "IsEquals", string expected = "True",
        string dataType = "", string path = "", string name = "", string hive = "HKEY_LOCAL_MACHINE", string key = "", bool is64 = true) =>
        new(source, property, op, expected, dataType, path, name, hive, key, is64, true, "");

    private static async Task<(int Exit, string Output)> RunScriptAsync(string text)
    {
        var (exit, stdout, _) = await WindowsPowerShellHost.RunAsync(text, null, CancellationToken.None, "apd-cmimport");
        return (exit, stdout.Trim());
    }

    private static async Task<bool> ParsesCleanlyAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "apd-cmparse-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(path, text, new UTF8Encoding(true));
        try
        {
            var probe = $"$t=$null;$e=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('{path}',[ref]$t,[ref]$e);Write-Output ('Errors ' + @($e).Count)";
            var (_, stdout, _) = await WindowsPowerShellHost.RunAsync(probe, null, CancellationToken.None, "apd-cmparse");
            return stdout.Contains("Errors 0", StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    // ---- the application document --------------------------------------

    [Fact]
    public void ReadsTheApplicationAndAnMsiDeploymentType()
    {
        var model = Read("single-msi");
        Assert.Equal("IMPTEST Single MSI", model.Name);
        Assert.Equal("Contoso Lab", model.Publisher);
        Assert.Equal("1.0", model.Version);
        Assert.Matches("^Application_", model.LogicalName);
        var dt = Assert.Single(model.DeploymentTypes);
        Assert.Equal("MSI", dt.Technology);
        Assert.Equal(1, dt.Priority);
        Assert.Equal("msiexec /i \"32bitcompat.msi\" /q", dt.InstallCommand);
        Assert.Matches(@"^msiexec /x \{4AF42476-4184-4857-AE0E-D8F4F794E34E\}", dt.UninstallCommand);
        Assert.Equal(@"\\cm01.contoso.com\ImpTest\msi", dt.ContentLocation);
        Assert.Contains("32bitcompat.msi", dt.ContentFiles);
        Assert.Equal("ProductCode", dt.Detection!.Method);
        Assert.Equal("{4AF42476-4184-4857-AE0E-D8F4F794E34E}", dt.Detection.ProductCode);
        Assert.Equal("System", dt.ExecutionContext);
    }

    [Fact]
    public void ReadsExitCodesAndRunTimeArguments()
    {
        var dt = Read("multi-lang").DeploymentTypes[1];
        Assert.Equal(60, dt.MaxRuntimeMins);
        Assert.Equal(15, dt.EstimatedRuntimeMins);
        Assert.Equal(3010, dt.ExitCodes.Single(e => e.Class == "SoftReboot").Code);
        Assert.Equal(1618, dt.ExitCodes.Single(e => e.Class == "FastRetry").Code);
    }

    [Fact]
    public void ReadsEveryClauseTypeOfAnEnhancedDetectionIntoAnAndTree()
    {
        var tree = Assert.IsType<DetectionGroup>(Read("clauses").DeploymentTypes[0].Detection!.Tree);
        Assert.Equal("And", tree.Operator);
        Assert.Equal(4, tree.Children.Count);
        var file = Assert.IsType<DetectionLeaf>(tree.Children[0]);
        var registry = Assert.IsType<DetectionLeaf>(tree.Children[1]);
        var key = Assert.IsType<DetectionLeaf>(tree.Children[2]);
        var folder = Assert.IsType<DetectionLeaf>(tree.Children[3]);
        Assert.Equal(("File", "Version", "GreaterEquals"), (file.Source, file.Property, file.Operator));
        Assert.Equal((@"C:\Program Files\ImpTest", "app.exe", false, "1.2.3.4"), (file.Path, file.Name, file.Is64Bit, file.Expected));
        Assert.Equal(("Registry", @"SOFTWARE\ImpTest", "Version", true, "Version", "HKEY_LOCAL_MACHINE"),
            (registry.Source, registry.Key, registry.Name, registry.Is64Bit, registry.DataType, registry.Hive));
        Assert.Equal(("RegistryKey", "Exists", "True", false), (key.Source, key.Property, key.Expected, key.Is64Bit));
        Assert.Equal(("Folder", "Exists", "data"), (folder.Source, folder.Property, folder.Name));
    }

    [Fact]
    public void ReadsOrAndNestedGroups()
    {
        var tree = Assert.IsType<DetectionGroup>(Read("or-detect").DeploymentTypes[0].Detection!.Tree);
        Assert.Equal("Or", tree.Operator);
        Assert.Equal(2, tree.Children.Count);
        Assert.IsType<DetectionLeaf>(tree.Children[0]);
        var inner = Assert.IsType<DetectionGroup>(tree.Children[1]);
        Assert.Equal("And", inner.Operator);
        Assert.Equal(2, inner.Children.Count);
    }

    [Fact]
    public void ReadsAScriptDetectionAndAUserContextDeploymentType()
    {
        var dt = Read("script-detect").DeploymentTypes[0];
        Assert.Equal("Script", dt.Detection!.Method);
        Assert.Equal("PowerShell", dt.Detection.ScriptLanguage);
        Assert.Contains("notepad.exe", dt.Detection.ScriptText);
        Assert.Equal("User", dt.ExecutionContext);
    }

    [Fact]
    public void KeepsThePriorityOrderOfSeveralDeploymentTypes()
    {
        var dts = Read("multi-lang").DeploymentTypes;
        Assert.Equal(["Lang - ja", "Lang - x64", "Lang - fallback"], dts.Select(d => d.Name));
        Assert.Equal([1, 2, 3], dts.Select(d => d.Priority));
    }

    [Fact]
    public void ReadsOperatingSystemLanguageMemoryAndProcessorRequirements()
    {
        var lang = Read("multi-lang").DeploymentTypes;
        Assert.Equal(("Global", "OSLanguage", "OneOf"), (lang[0].Requirements[0].Kind, lang[0].Requirements[0].LogicalName, lang[0].Requirements[0].Operator));
        Assert.Equal(["1041"], lang[0].Requirements[0].Values);
        Assert.Equal("OsPlatform", lang[1].Requirements[0].Kind);
        Assert.Equal(["Windows/All_x64_Windows_10_higher"], lang[1].Requirements[0].PlatformIds);
        Assert.Empty(lang[2].Requirements);

        var misc = Read("misc-reqs").DeploymentTypes[0].Requirements;
        Assert.Equal(["OsPlatform:", "Global:TotalPhysicalMemory", "Global:NumberOfProcessors"], misc.Select(r => r.Kind + ":" + r.LogicalName));
        Assert.Equal(["4294967296"], misc[1].Values);
    }

    [Fact]
    public void ReadsDependenciesAndSupersedenceAsReferences()
    {
        var dependent = Read("dependent").DeploymentTypes[0];
        var group = Assert.Single(dependent.Dependencies);
        Assert.Equal("Needs arch", group.GroupName);
        Assert.Equal("Required", group.Targets[0].DesiredState);
        Assert.Matches("^DeploymentType_", group.Targets[0].DeploymentTypeLogical);
        Assert.Equal("Prohibited", Read("newer").DeploymentTypes[0].Supersedes[0].Targets[0].DesiredState);
    }

    [Fact]
    public void MarksATechnologyThatWin32AppsCannotCarry()
    {
        var xml = FixtureXml("script-detect").Replace("<Technology>Script</Technology>", "<Technology>AppV5X</Technology>");
        Assert.Contains("AppV5X", CmApplicationDigestReader.Read(xml).DeploymentTypes[0].UnsupportedReason);
    }

    [Fact]
    public void RefusesADocumentThatIsNotAnApplicationDigest()
    {
        var ex = Assert.Throws<InvalidDataException>(() => CmApplicationDigestReader.Read("<root/>"));
        Assert.Contains("AppMgmtDigest", ex.Message);
    }

    [Fact]
    public void OrdersTheDeploymentTypesByTheApplicationList()
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(FixtureXml("multi-arch"));
        var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("a", "http://schemas.microsoft.com/SystemCenterConfigurationManager/2009/AppMgmtDigest");
        var list = doc.SelectSingleNode("/a:AppMgmtDigest/a:Application/a:DeploymentTypes", ns)!;
        var first = list.FirstChild!;
        list.RemoveChild(first);
        list.AppendChild(first);
        var model = CmApplicationDigestReader.Read(doc.OuterXml);
        Assert.Equal(["Multi - x86", "Multi - x64"], model.DeploymentTypes.Select(d => d.Name));
        Assert.Equal([1, 2], model.DeploymentTypes.Select(d => d.Priority));
    }

    [Fact]
    public void KeepsTheBitnessOfAScriptDetector()
    {
        var xml = Regex.Replace(FixtureXml("script-detect"), "(<Arg Name=\"RunAs32Bit\" Type=\"Boolean\">)false(</Arg></Args></DetectAction>)", "${1}true${2}");
        var model = CmApplicationDigestReader.Read(xml);
        Assert.True(model.DeploymentTypes[0].Detection!.RunAs32Bit);
        var rule = CmImportPlanner.Plan(model).DeploymentTypes[0].Manifest!.IntuneRules.Single(r => r["ruleType"]!.GetValue<string>() == "detection");
        Assert.True(rule["runAs32Bit"]!.GetValue<bool>());
        Assert.False(CmImportPlanner.Plan(Read("script-detect")).DeploymentTypes[0].Manifest!.IntuneRules[0]["runAs32Bit"]!.GetValue<bool>());
    }

    // ---- detection ------------------------------------------------------

    [Fact]
    public void MapsAnMsiProductCodeToAProductCodeRule()
    {
        var result = CmDetectionConverter.Convert(Read("single-msi").DeploymentTypes[0].Detection!);
        Assert.Equal("ProductCode", result.Mode);
        var rule = result.Rules[0];
        Assert.Equal("#microsoft.graph.win32LobAppProductCodeRule", rule["@odata.type"]!.GetValue<string>());
        Assert.Equal("{4AF42476-4184-4857-AE0E-D8F4F794E34E}", rule["productCode"]!.GetValue<string>());
        Assert.Equal("detection", rule["ruleType"]!.GetValue<string>());
    }

    [Fact]
    public void MapsAFlatAndOfFileRegistryAndFolderClausesToNativeRulesWithTheRightViewFlags()
    {
        var result = CmDetectionConverter.Convert(Read("clauses").DeploymentTypes[0].Detection!);
        Assert.Equal("Native", result.Mode);
        Assert.Equal(4, result.Rules.Count);
        var (file, registry, key, folder) = (result.Rules[0], result.Rules[1], result.Rules[2], result.Rules[3]);
        Assert.Equal("#microsoft.graph.win32LobAppFileSystemRule", file["@odata.type"]!.GetValue<string>());
        Assert.Equal(("version", "greaterThanOrEqual", "1.2.3.4"), (file["operationType"]!.GetValue<string>(), file["operator"]!.GetValue<string>(), file["comparisonValue"]!.GetValue<string>()));
        Assert.True(file["check32BitOn64System"]!.GetValue<bool>());
        Assert.Equal("#microsoft.graph.win32LobAppRegistryRule", registry["@odata.type"]!.GetValue<string>());
        Assert.Equal((@"HKEY_LOCAL_MACHINE\SOFTWARE\ImpTest", "Version", "version"), (registry["keyPath"]!.GetValue<string>(), registry["valueName"]!.GetValue<string>(), registry["operationType"]!.GetValue<string>()));
        Assert.False(registry["check32BitOn64System"]!.GetValue<bool>());
        Assert.Equal("exists", key["operationType"]!.GetValue<string>());
        Assert.True(key["check32BitOn64System"]!.GetValue<bool>());
        Assert.Null(key["valueName"]);
        Assert.Equal(("exists", "data"), (folder["operationType"]!.GetValue<string>(), folder["fileOrFolderName"]!.GetValue<string>()));
    }

    [Fact]
    public void CarriesAPowerShellDetectionScriptAsOneScriptRule()
    {
        var result = CmDetectionConverter.Convert(Read("script-detect").DeploymentTypes[0].Detection!);
        Assert.Equal("Script", result.Mode);
        var rule = Assert.Single(result.Rules);
        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", rule["@odata.type"]!.GetValue<string>());
        Assert.Contains("notepad.exe", Decode(rule));
        Assert.False(rule["enforceSignatureCheck"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TurnsOrAndGroupsIntoOneScriptRuleWithAReviewFinding()
    {
        var result = CmDetectionConverter.Convert(Read("or-detect").DeploymentTypes[0].Detection!);
        Assert.Equal("ExpressionScript", result.Mode);
        Assert.Single(result.Rules);
        Assert.Contains(result.Findings, f => f.Code == "DetectionConvertedToScript");
        Assert.True(await ParsesCleanlyAsync(Decode(result.Rules[0])));
    }

    [Fact]
    public void RefusesTheScriptConversionWhenNativeRulesOnlyAreRequested()
    {
        var result = CmDetectionConverter.Convert(Read("or-detect").DeploymentTypes[0].Detection!, nativeOnly: true);
        Assert.Empty(result.Rules);
        Assert.Equal("DetectionNeedsScript", Assert.Single(result.Findings, f => f.Severity == "Blocking").Code);
    }

    [Fact]
    public void MovesAPrefixComparisonAndAWildcardFileNameToTheScript()
    {
        var prefix = Leaf("Registry", "Value", "BeginsWith", "Win", "String", key: @"SOFTWARE\X", name: "N");
        Assert.Equal("ExpressionScript", CmDetectionConverter.Convert(new CmDetection("Enhanced", prefix, "", "", false, "", "")).Mode);
        var wild = Leaf("File", "Exists", path: @"C:\X", name: "*.dll");
        Assert.Equal("ExpressionScript", CmDetectionConverter.Convert(new CmDetection("Enhanced", wild, "", "", false, "", "")).Mode);
    }

    [Fact]
    public void BlocksAClauseItCannotRead()
    {
        var bad = new DetectionLeaf("WQL", "Value", "", "", "", "", "", "", "", false, false, "not converted");
        var result = CmDetectionConverter.Convert(new CmDetection("Enhanced", bad, "", "", false, "", ""));
        Assert.Equal("DetectionNotConvertible", Assert.Single(result.Findings, f => f.Severity == "Blocking").Code);
    }

    [Fact]
    public void BlocksADetectionExpressionThatHasNoConditions()
    {
        var result = CmDetectionConverter.Convert(new CmDetection("Enhanced", new DetectionGroup("And", []), "", "", false, "", ""));
        Assert.Empty(result.Rules);
        Assert.Equal("DetectionMissing", Assert.Single(result.Findings, f => f.Severity == "Blocking").Code);
    }

    [Fact]
    public void DoesNotConvertARegistryKeyComparisonThatIsNotAnExistenceTest()
    {
        var xml = Regex.Replace(FixtureXml("clauses"), "(<Operator>)Equals(</Operator><Operands><SettingReference [^>]*SettingSourceType=\"RegistryKey\")", "${1}GreaterThan${2}");
        var dt = CmImportPlanner.Plan(CmApplicationDigestReader.Read(xml)).DeploymentTypes[0];
        Assert.False(dt.Convert);
        Assert.Contains(dt.Findings, f => f.Code == "DetectionNotConvertible");
    }

    [Fact]
    public async Task DoublesTypographicSingleQuotesInGeneratedScripts()
    {
        var name = "a\u2019b\u2018c.txt";
        var text = CmDetectionConverter.ExpressionScript(Leaf("File", "Exists", path: @"C:\X", name: name));
        Assert.True(await ParsesCleanlyAsync(text));
        Assert.Contains("a\u2019\u2019b\u2018\u2018c.txt", text);
    }

    [Fact]
    public async Task QuotesValuesWithSingleQuotes()
    {
        var text = CmDetectionConverter.ExpressionScript(Leaf("File", "Exists", path: @"C:\O'Brien", name: "a'b.txt"));
        Assert.True(await ParsesCleanlyAsync(text));
        Assert.Contains("O''Brien", text);
    }

    private static readonly DetectionLeaf System32Present =
        Leaf("File", "Exists", expected: "True", path: @"%SystemRoot%\System32", name: "kernel32.dll");

    private static readonly DetectionLeaf System32Absent =
        Leaf("File", "Exists", expected: "True", path: @"%SystemRoot%\System32", name: "no-such-file-imptest.dll");

    [Fact]
    public async Task TheScriptExitsZeroWithOutputWhenAnOrHasOneTrueBranch()
    {
        var run = await RunScriptAsync(CmDetectionConverter.ExpressionScript(new DetectionGroup("Or", [System32Absent, System32Present])));
        Assert.Equal((0, "Detected"), run);
    }

    [Fact]
    public async Task TheScriptExitsOneWhenAnAndHasOneFalseBranch()
    {
        var run = await RunScriptAsync(CmDetectionConverter.ExpressionScript(new DetectionGroup("And", [System32Present, System32Absent])));
        Assert.Equal((1, ""), run);
    }

    [Fact]
    public async Task TheScriptEvaluatesNestedGroupsAbsenceRegistryValuesAndVersions()
    {
        const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var present = Leaf("RegistryKey", "Exists", expected: "True", key: nt);
        var absent = Leaf("RegistryKey", "Exists", expected: "False", key: @"SOFTWARE\ImpTestNoSuchKey");
        var build = Leaf("Registry", "Value", "GreaterEquals", "1", "Int64", key: nt, name: "CurrentBuildNumber");
        var product = Leaf("Registry", "Value", "BeginsWith", "Windows", "String", key: nt, name: "ProductName");
        var version = Leaf("File", "Version", "GreaterEquals", "6.0.0.0", "Version", @"%SystemRoot%\System32", "kernel32.dll");
        var tree = new DetectionGroup("And", [present, absent, new DetectionGroup("Or", [build, product]), version]);
        Assert.Equal(0, (await RunScriptAsync(CmDetectionConverter.ExpressionScript(tree))).Exit);

        var tooNew = Leaf("Registry", "Value", "GreaterEquals", "99999999", "Int64", key: nt, name: "CurrentBuildNumber");
        Assert.Equal(1, (await RunScriptAsync(CmDetectionConverter.ExpressionScript(tooNew))).Exit);
    }

    [Fact]
    public async Task TheScriptReadsTheThirtyTwoBitRegistryViewWhenTheClauseIsThirtyTwoBit()
    {
        // HKLM\SOFTWARE\WOW6432Node\Microsoft exists on 64-bit Windows; the 32-bit view of SOFTWARE\Microsoft is that key.
        var wow = Leaf("RegistryKey", "Exists", expected: "True", key: @"SOFTWARE\Microsoft", is64: false);
        Assert.Equal(0, (await RunScriptAsync(CmDetectionConverter.ExpressionScript(wow))).Exit);
        var missing32 = Leaf("RegistryKey", "Exists", expected: "True", key: @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ImpTestNoSuchSubKey", is64: false);
        Assert.Equal(1, (await RunScriptAsync(CmDetectionConverter.ExpressionScript(missing32))).Exit);
    }

    // ---- requirements ---------------------------------------------------

    private static CmRequirementModel RequirementModel(string fixture, int index) =>
        CmRequirementConverter.Convert(Read(fixture).DeploymentTypes[index].Requirements);

    [Fact]
    public void TakesArchitectureMemoryAndProcessorsNatively()
    {
        var model = RequirementModel("misc-reqs", 0);
        Assert.Equal(["x64"], model.Architectures);
        Assert.Equal(4096, model.Native["MinimumMemoryInMB"]);
        Assert.Equal(2, model.Native["MinimumNumberOfProcessors"]);
        Assert.Empty(model.ScriptOnly);
        Assert.True(model.Evaluable);
        Assert.True(model.HasRules);
    }

    [Fact]
    public void SendsALanguageRuleToTheScript()
    {
        var model = RequirementModel("multi-lang", 0);
        Assert.Single(model.Predicates);
        var language = Assert.IsType<CmLanguagePredicate>(Assert.Single(model.ScriptOnly));
        Assert.Equal([1041], language.Values);
        Assert.Null(model.Architectures);
    }

    [Fact]
    public void ReportsADeploymentTypeWithoutRules()
    {
        var model = CmRequirementConverter.Convert([]);
        Assert.False(model.HasRules);
        Assert.True(model.Evaluable);
    }

    private static CmRequirementRule Os(string op, string id) => new("OsPlatform", id, op, [id], "", [], "", "");

    private static CmRequirementRule Global(string name, string value, string op = "GreaterEquals") => new("Global", name, op, [], name, [value], "Int64", "");

    [Fact]
    public void SendsAWindows11FloorAndANoneOfRuleToTheScript()
    {
        Assert.Single(CmRequirementConverter.Convert([Os("OneOf", "Windows/All_x64_Windows_11_higher")]).ScriptOnly);
        Assert.Single(CmRequirementConverter.Convert([Os("NoneOf", "Windows/All_ARM64_Windows_10_higher")]).ScriptOnly);
    }

    [Fact]
    public void ListsARuleItCannotEvaluateAsUnrecognized()
    {
        var custom = new CmRequirementRule("Unsupported", "Custom thing", "", [], "", [], "", "custom");
        var model = CmRequirementConverter.Convert([custom, Global("FreeDiskSpace", "5")]);
        Assert.False(model.Evaluable);
        Assert.Equal(2, model.Unrecognized.Count);
    }

    [Fact]
    public void RefusesAnOperatingSystemTheWin32AppTypeCannotReach() =>
        Assert.False(CmRequirementConverter.Convert([Os("OneOf", "Windows/All_x64_Windows_7_Client")]).Evaluable);

    [Fact]
    public void KeepsTheLargerMemoryAndProcessorMinimum()
    {
        var model = CmRequirementConverter.Convert([
            Global("TotalPhysicalMemory", "4294967296"), Global("TotalPhysicalMemory", "2147483648"),
            Global("NumberOfProcessors", "4"), Global("NumberOfProcessors", "2")]);
        Assert.Equal(4096, model.Native["MinimumMemoryInMB"]);
        Assert.Equal(4, model.Native["MinimumNumberOfProcessors"]);
        Assert.Equal(4096, CmRequirementConverter.Convert([Global("TotalPhysicalMemory", "2147483648"), Global("TotalPhysicalMemory", "4294967296")]).Native["MinimumMemoryInMB"]);
    }

    [Fact]
    public void NeedsNothingForTheFirstVariant() =>
        Assert.Equal("None", CmRequirementConverter.Exclusion(RequirementModel("multi-arch", 0), []).Status);

    [Fact]
    public void NeedsNoScriptWhenTheArchitecturesAreDisjoint() =>
        Assert.Equal("Native", CmRequirementConverter.Exclusion(RequirementModel("multi-arch", 1), [RequirementModel("multi-arch", 0)]).Status);

    [Fact]
    public void NeedsAScriptWhenAnEarlierVariantIsNotDisjoint()
    {
        var result = CmRequirementConverter.Exclusion(RequirementModel("multi-lang", 1), [RequirementModel("multi-lang", 0)]);
        Assert.Equal("Script", result.Status);
        Assert.Single(result.EarlierSets);
        var fallback = CmRequirementConverter.Exclusion(RequirementModel("multi-lang", 2), [RequirementModel("multi-lang", 0), RequirementModel("multi-lang", 1)]);
        Assert.Equal("Script", fallback.Status);
        Assert.Equal(2, fallback.EarlierSets.Count);
    }

    [Fact]
    public void ReportsAVariantThatAnUnconditionalEarlierVariantShadows() =>
        Assert.Equal("Unreachable", CmRequirementConverter.Exclusion(RequirementModel("unreachable", 1), [RequirementModel("unreachable", 0)]).Status);

    [Fact]
    public void ReportsAnExclusionItCannotProve()
    {
        var custom = new CmRequirementRule("Unsupported", "Custom", "", [], "", [], "", "custom");
        var earlier = CmRequirementConverter.Convert([custom]);
        Assert.Equal("Unprovable", CmRequirementConverter.Exclusion(RequirementModel("multi-arch", 0), [earlier]).Status);
    }

    private static CmOsPlatformPredicate ArchPredicate(string arch) => new([new CmPlatformPair(arch, 10240)], false);

    [Fact]
    public async Task TheVariantScriptPrintsFalseWhenAnEarlierVariantAppliesToThisDevice()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var run = await RunScriptAsync(CmRequirementConverter.VariantScript(null, [[ArchPredicate(arch)]]));
        Assert.Equal((0, "false"), run);
    }

    [Fact]
    public async Task TheVariantScriptPrintsTrueWhenNoEarlierVariantApplies()
    {
        Assert.Equal((0, "true"), await RunScriptAsync(CmRequirementConverter.VariantScript(null, [[ArchPredicate("x86")]])));
        Assert.Equal((0, "true"), await RunScriptAsync(CmRequirementConverter.VariantScript(null, [[new CmLanguagePredicate("-in", [1])]])));
    }

    [Fact]
    public async Task TheVariantScriptPrintsTrueWhenNothingIsRequired() =>
        Assert.Equal((0, "true"), await RunScriptAsync(CmRequirementConverter.VariantScript()));

    [Fact]
    public async Task TheVariantScriptParses() =>
        Assert.True(await ParsesCleanlyAsync(CmRequirementConverter.VariantScript(
            [new CmLanguagePredicate("-in", [1041, 1033]), new CmMemoryPredicate("-ge", 4294967296), new CmProcessorsPredicate("-ge", 2)],
            [[ArchPredicate("x64")], [new CmOsPlatformPredicate([new CmPlatformPair("x86", 10240), new CmPlatformPair("", 22000)], true)]])));

    // ---- setup file and return codes ------------------------------------

    [Theory]
    [InlineData("install.bat", "install.bat|uninstall.bat", "install.bat")]
    [InlineData("msiexec /i \"Tool Setup.msi\" /qn", "Tool Setup.msi|a.txt", "Tool Setup.msi")]
    [InlineData("\"setup.exe\" /S", "SETUP.EXE", "SETUP.EXE")]
    [InlineData("powershell.exe -File .\\run.ps1", "run.ps1", "run.ps1")]
    [InlineData("cmd /c something", "readme.txt|x.exe", "x.exe")]
    [InlineData("cmd /c something", "readme.txt", "")]
    [InlineData(".\\bin\\setup.exe /S", "bin\\setup.exe|a.txt", "bin\\setup.exe")]
    [InlineData("setup.exe /S", "tools\\setup.exe|a.txt", "tools\\setup.exe")]
    [InlineData("msiexec /i \"pkg/app.msi\" /qn", "pkg\\app.msi", "pkg\\app.msi")]
    [InlineData("setup.exe", "a\\setup.exe|b\\setup.exe", "")]
    public void FindsTheSetupFileTheCommandRuns(string command, string files, string expected) =>
        Assert.Equal(expected, CmImportPlanner.SetupFile(command, files.Split('|')));

    [Fact]
    public void MapsTheExitCodeClassesAndSuppliesDefaults()
    {
        var codes = CmImportPlanner.ReturnCodes([new(0, "Success"), new(3010, "SoftReboot"), new(1641, "HardReboot"), new(1618, "FastRetry"), new(5, "Failure")]);
        Assert.Equal(["0=success", "3010=softReboot", "1641=hardReboot", "1618=retry", "5=failed"],
            codes.Select(c => $"{c["returnCode"]}={c["type"]!.GetValue<string>()}"));
        Assert.Equal(5, CmImportPlanner.ReturnCodes([]).Count);
    }

    // ---- the plan -------------------------------------------------------

    private static JsonObject[] RequirementRules(CmDeploymentTypePlan plan) =>
        plan.Manifest!.IntuneRules.Where(r => r["ruleType"]!.GetValue<string>() == "requirement").ToArray();

    [Fact]
    public void PlansASingleDeploymentTypeUnderTheApplicationName()
    {
        var plan = CmImportPlanner.Plan(Read("clauses"));
        var dt = Assert.Single(plan.DeploymentTypes);
        Assert.True(dt.Convert);
        Assert.Equal("IMPTEST Clauses", dt.IntuneName);
        var m = dt.Manifest!;
        Assert.Equal(("IMPTEST Clauses", "install.bat", "install.bat", "InstallForSystem"), (m.AppName, m.SetupFile, m.InstallCommandLine, m.InstallationBehaviorType));
        Assert.Equal(4, m.IntuneRules.Count);
        Assert.Equal("x86,x64", m.IntuneAllowedArchitectures);
        Assert.Matches("^cm:Application_", m.ApplicationId);
        Assert.Matches("^DeploymentType_", m.ProfileId);
        Assert.Equal(45, m.Timing.MaximumRuntimeMins);
        Assert.False(m.Timing.IntuneApplied);
        Assert.Empty(plan.Findings);
    }

    [Fact]
    public void MapsAUserContextDeploymentTypeAndKeepsTheInteractionFlagOnlyForSystemInstalls()
    {
        var dt = CmImportPlanner.Plan(Read("script-detect")).DeploymentTypes[0];
        Assert.Equal("InstallForUser", dt.Manifest!.InstallationBehaviorType);
        Assert.False(dt.Manifest.RequireUserInteraction);
        Assert.Contains(dt.Findings, f => f.Code == "UserInteractionPossible");

        var xml = Regex.Replace(FixtureXml("clauses"), "(<Arg Name=\"RequiresUserInteraction\" Type=\"Boolean\">)false", "${1}true");
        Assert.True(CmImportPlanner.Plan(CmApplicationDigestReader.Read(xml)).DeploymentTypes[0].Manifest!.RequireUserInteraction);
    }

    [Fact]
    public void MakesOneIntuneAppPerDeploymentTypeNamedWithTheDeploymentType()
    {
        var plan = CmImportPlanner.Plan(Read("multi-arch"));
        Assert.Equal(["IMPTEST Multi Arch - Multi - x64", "IMPTEST Multi Arch - Multi - x86"], plan.DeploymentTypes.Select(d => d.IntuneName));
        var (x64, x86) = (plan.DeploymentTypes[0].Manifest!, plan.DeploymentTypes[1].Manifest!);
        Assert.Equal(("x64", "x86"), (x64.IntuneAllowedArchitectures, x86.IntuneAllowedArchitectures));
        Assert.NotEqual(x64.ProfileId, x86.ProfileId);
        Assert.Equal(x64.ApplicationId, x86.ApplicationId);
        Assert.Equal("install.bat", x64.SetupFile);
        Assert.Empty(RequirementRules(plan.DeploymentTypes[1]));
        Assert.Contains(plan.Findings, f => f.Code == "VariantApps");
    }

    [Fact]
    public void AddsARequirementScriptToKeepFirstMatchWins()
    {
        var plan = CmImportPlanner.Plan(Read("multi-lang"));
        var (ja, x64, fallback) = (plan.DeploymentTypes[0], plan.DeploymentTypes[1], plan.DeploymentTypes[2]);
        Assert.Single(RequirementRules(ja));
        var requirement = Assert.Single(RequirementRules(x64));
        Assert.Equal(("boolean", "equal", "true"), (requirement["operationType"]!.GetValue<string>(), requirement["operator"]!.GetValue<string>(), requirement["comparisonValue"]!.GetValue<string>()));
        Assert.Contains("1041", Decode(requirement));
        Assert.Contains("x64", Decode(Assert.Single(RequirementRules(fallback))));
    }

    [Fact]
    public void LeavesOutADeploymentTypeThatConfigMgrCanNeverChoose()
    {
        var plan = CmImportPlanner.Plan(Read("unreachable"));
        Assert.True(plan.DeploymentTypes[0].Convert);
        Assert.False(plan.DeploymentTypes[1].Convert);
        Assert.Contains(plan.DeploymentTypes[1].Findings, f => f.Code == "UnreachableDeploymentType");
        Assert.Null(plan.DeploymentTypes[1].Manifest);
    }

    [Fact]
    public void BlocksAnUnprovableExclusionUnlessTheStrategyIsAssignByGroup()
    {
        var xml = Regex.Replace(FixtureXml("multi-arch"),
            @"<OperatingSystemExpression>\s*<Operator>OneOf</Operator>\s*<Operands>\s*<RuleExpression RuleId=""Windows/All_x64_Windows_10_higher""\s*/>\s*</Operands>\s*</OperatingSystemExpression>", "<Foo/>");
        var model = CmApplicationDigestReader.Read(xml);
        Assert.Equal("Unsupported", model.DeploymentTypes[0].Requirements[0].Kind);
        var blocked = CmImportPlanner.Plan(model);
        Assert.False(blocked.DeploymentTypes[1].Convert);
        Assert.Contains(blocked.DeploymentTypes[1].Findings, f => f.Code == "ExclusivityUnprovable");
        var byGroup = CmImportPlanner.Plan(model, new CmImportOptions(CmImportPlanner.AssignByGroup));
        Assert.True(byGroup.DeploymentTypes[1].Convert);
        Assert.Contains(byGroup.DeploymentTypes[1].Findings, f => f.Code == "AssignByGroupRequired");
    }

    [Fact]
    public void DoesNotAddExclusionScriptsUnderAssignByGroup()
    {
        var plan = CmImportPlanner.Plan(Read("multi-lang"), new CmImportOptions(CmImportPlanner.AssignByGroup));
        Assert.Empty(RequirementRules(plan.DeploymentTypes[2]));
        Assert.Contains(plan.DeploymentTypes[1].Findings, f => f.Code == "AssignByGroupRequired");
    }

    [Fact]
    public void ListsDependenciesAndSupersedenceWithoutCreatingThem()
    {
        var dependent = CmImportPlanner.Plan(Read("dependent"));
        var relationship = Assert.Single(dependent.Relationships);
        Assert.Equal(("Dependency", "Required"), (relationship.Type, relationship.DesiredState));
        Assert.Contains(dependent.Findings, f => f.Code == "RelationshipsNotCreated");
        Assert.Equal("Supersedence", CmImportPlanner.Plan(Read("newer")).Relationships[0].Type);
    }

    [Fact]
    public void TakesMinimumHardwareValuesNativelyAndKeepsTheReturnCodes()
    {
        var m = CmImportPlanner.Plan(Read("misc-reqs")).DeploymentTypes[0].Manifest!;
        Assert.Equal(4096, m.IntuneMinimums["MinimumMemoryInMB"]);
        Assert.Equal(2, m.IntuneMinimums["MinimumNumberOfProcessors"]);
        Assert.Equal(3010, m.IntuneReturnCodes.Single(c => c["type"]!.GetValue<string>() == "softReboot")["returnCode"]!.GetValue<int>());
        Assert.Equal("basedOnReturnCode", m.IntuneRestartBehavior);
    }

    [Fact]
    public void DoesNotConvertATechnologyWin32AppsCannotCarry()
    {
        var xml = FixtureXml("script-detect").Replace("<Technology>Script</Technology>", "<Technology>AppV5X</Technology>");
        var dt = CmImportPlanner.Plan(CmApplicationDigestReader.Read(xml)).DeploymentTypes[0];
        Assert.False(dt.Convert);
        Assert.Contains(dt.Findings, f => f.Code == "TechnologyNotConverted");
    }

    [Fact]
    public void DoesNotConvertADeploymentTypeWhoseContentHoldsNoSetupFile()
    {
        var xml = Regex.Replace(Regex.Replace(FixtureXml("clauses"), @"<File Name=""install.bat"" Size=""\d+""\s*/>", ""), @"<File Name=""uninstall.bat"" Size=""\d+""\s*/>", "");
        var dt = CmImportPlanner.Plan(CmApplicationDigestReader.Read(xml)).DeploymentTypes[0];
        Assert.False(dt.Convert);
        Assert.Contains(dt.Findings, f => f.Code == "SetupFileNotFound");
    }

    [Fact]
    public void UsesACommandThatDoesNothingWhenConfigMgrStoresNoUninstall()
    {
        var xml = FixtureXml("clauses").Replace("<UninstallSetting>SameAsInstall</UninstallSetting>", "<UninstallSetting>NoneRequired</UninstallSetting>");
        var dt = CmImportPlanner.Plan(CmApplicationDigestReader.Read(xml)).DeploymentTypes[0];
        Assert.Equal("cmd.exe /c exit 0", dt.Manifest!.UninstallCommandLine);
        Assert.Contains(dt.Findings, f => f.Code == "NoUninstallCommand");
    }

    private const string RuleNamespace = "http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/06/14/Rules";

    private static string OsRuleXml(string platformId) =>
        $"<Rule xmlns=\"{RuleNamespace}\" id=\"Rule_extra\" Severity=\"None\" NonCompliantWhenSettingIsNotFound=\"false\"><Annotation><DisplayName Text=\"extra\"/><Description Text=\"\"/></Annotation><OperatingSystemExpression><Operator>OneOf</Operator><Operands><RuleExpression RuleId=\"{platformId}\"/></Operands></OperatingSystemExpression></Rule>";

    private static string DiskRuleXml() =>
        $"<Rule xmlns=\"{RuleNamespace}\" id=\"Rule_disk\" Severity=\"None\" NonCompliantWhenSettingIsNotFound=\"false\"><Annotation><DisplayName Text=\"Disk\"/><Description Text=\"\"/></Annotation><Expression><Operator>GreaterEquals</Operator><Operands><GlobalSettingReference AuthoringScopeId=\"GLOBAL\" LogicalName=\"FreeDiskSpace\" DataType=\"Int64\" SettingLogicalName=\"x\" SettingSourceType=\"CIM\" Method=\"Value\"/><ConstantValue Value=\"5\" DataType=\"Int64\"/></Operands></Expression></Rule>";

    [Fact]
    public void DoesNotConvertADeploymentTypeWhoseOperatingSystemRulesShareNoArchitecture()
    {
        var xml = FixtureXml("misc-reqs").Replace("</Requirements>", OsRuleXml("Windows/All_x86_Windows_10_higher") + "</Requirements>");
        var model = CmApplicationDigestReader.Read(xml);
        Assert.Empty(CmRequirementConverter.Convert(model.DeploymentTypes[0].Requirements).Architectures!);
        var dt = CmImportPlanner.Plan(model).DeploymentTypes[0];
        Assert.False(dt.Convert);
        Assert.Contains(dt.Findings, f => f.Code == "ArchitectureConflict");
        Assert.DoesNotContain(dt.Findings, f => f.Code == "ArchitectureNotConstrained");
    }

    [Fact]
    public void DoesNotConvertADeploymentTypeWithARequirementItCannotTranslateUnlessAllowed()
    {
        var model = CmApplicationDigestReader.Read(FixtureXml("misc-reqs").Replace("</Requirements>", DiskRuleXml() + "</Requirements>"));
        var blocked = CmImportPlanner.Plan(model).DeploymentTypes[0];
        Assert.False(blocked.Convert);
        Assert.Equal("Blocking", blocked.Findings.Single(f => f.Code == "RequirementsNotTranslated").Severity);
        var allowed = CmImportPlanner.Plan(model, new CmImportOptions(AllowUntranslatedRequirements: true)).DeploymentTypes[0];
        Assert.True(allowed.Convert);
        Assert.Equal("Review", allowed.Findings.Single(f => f.Code == "RequirementsNotTranslated").Severity);
    }

    [Fact]
    public void WritesAReadableReport()
    {
        var text = CmImportPlanner.Report(CmImportPlanner.Plan(Read("multi-lang")));
        Assert.Contains("Deployment type 1: Lang - ja", text);
        Assert.Contains("Deployment type 3: Lang - fallback", text);
        Assert.Contains("VariantExclusionScript", text);
    }

    // ---- the stage ------------------------------------------------------

    private string Source(params string[] names)
    {
        var source = Path.Combine(_dir, "source-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var name in names)
        {
            var folder = Path.Combine(source, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "install.bat"), "exit /b 0");
            File.WriteAllText(Path.Combine(folder, "uninstall.bat"), "exit /b 0");
        }
        return source;
    }

    [Fact]
    public void CopiesContentPerDeploymentTypeAndWritesTheManifestsAndReports()
    {
        var source = Source("x64", "x86");
        var plan = CmImportPlanner.Plan(Read("multi-arch"));
        foreach (var dt in plan.DeploymentTypes) dt.ContentLocation = Path.Combine(source, dt.Priority == 1 ? "x64" : "x86");
        var stage = CmImportStage.Export(plan, Path.Combine(_dir, "out"));
        Assert.Equal(2, stage.Staged.Count);
        foreach (var item in stage.Staged)
        {
            Assert.True(File.Exists(Path.Combine(item.ContentFolder, "install.bat")));
            var manifest = JsonNode.Parse(File.ReadAllText(item.ManifestPath))!;
            Assert.Equal(item.IntuneName, manifest["AppName"]!.GetValue<string>());
            Assert.Equal(item.ContentFolder, manifest["ContentRoot"]!.GetValue<string>());
            Assert.NotEmpty(manifest["IntuneRules"]!.AsArray());
        }
        foreach (var name in new[] { "conversion-report.txt", "conversion-plan.json", "relationships.json" })
            Assert.True(File.Exists(Path.Combine(stage.ApplicationFolder, name)), name);
    }

    [Fact]
    public void MirrorsTheContentSoARepeatRunDropsFilesTheSiteNoLongerHolds()
    {
        var source = Source("x64");
        var plan = CmImportPlanner.Plan(Read("clauses"));
        plan.DeploymentTypes[0].ContentLocation = Path.Combine(source, "x64");
        var stage = CmImportStage.Export(plan, Path.Combine(_dir, "out"));
        var stale = Path.Combine(stage.Staged[0].ContentFolder, "old-file.txt");
        File.WriteAllText(stale, "x");
        CmImportStage.Export(plan, Path.Combine(_dir, "out"));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(Path.Combine(stage.Staged[0].ContentFolder, "install.bat")));
    }

    [Fact]
    public void StopsWhenTheSetupFileIsMissingFromTheCopiedContent()
    {
        var plan = CmImportPlanner.Plan(Read("clauses"));
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "other.txt"), "x");
        plan.DeploymentTypes[0].ContentLocation = empty;
        var ex = Assert.Throws<InvalidOperationException>(() => CmImportStage.Export(plan, Path.Combine(_dir, "out")));
        Assert.Contains("setup file", ex.Message);
    }

    [Fact]
    public void SkipsTheCopyOnRequestAndLeavesOutADeploymentTypeThatWasNotConverted()
    {
        var plan = CmImportPlanner.Plan(Read("unreachable"));
        Assert.Single(CmImportStage.Export(plan, Path.Combine(_dir, "out"), skipContent: true).Staged);
    }

    [Fact]
    public void RefusesTwoDeploymentTypesThatMapToOneFolder()
    {
        var plan = CmImportPlanner.Plan(Read("multi-arch"));
        plan.DeploymentTypes[0].Name = "A/B";
        plan.DeploymentTypes[1].Name = "A:B";
        var ex = Assert.Throws<InvalidOperationException>(() => CmImportStage.Export(plan, Path.Combine(_dir, "collision"), skipContent: true));
        Assert.Contains("same folder", ex.Message);
    }

    [Fact]
    public void RefusesToMirrorIntoAFolderNotNamedContent() =>
        Assert.Throws<InvalidOperationException>(() => CmImportStage.CopyContent(_dir, Path.Combine(_dir, "elsewhere")));

    [Fact]
    public void WritesTheIconBesideTheManifest()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        var plan = CmImportPlanner.Plan(Read("clauses"));
        var stage = CmImportStage.Export(plan, Path.Combine(_dir, "icon"), skipContent: true, iconBase64: png);
        Assert.True(File.Exists(Path.Combine(stage.Staged[0].Folder, "app-icon.png")));
        Assert.Equal("app-icon.png", JsonNode.Parse(File.ReadAllText(stage.Staged[0].ManifestPath))!["Icon"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("  name.  ", "name")]
    [InlineData("", "item")]
    [InlineData("...", "item")]
    public void SafeNamesAreFolderNames(string name, string expected) => Assert.Equal(expected, CmImportStage.SafeName(name));
}
