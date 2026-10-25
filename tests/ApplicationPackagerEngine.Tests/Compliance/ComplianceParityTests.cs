using System.Diagnostics;
using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>
/// The service's drift tag, drift state, stale rule and builder arguments compared with the
/// source module's own functions, run read-only in a child Windows PowerShell on fixed cases.
/// When the module is not on this computer, its recorded results stand in, and each test asserts
/// that its cases ran. Differences by design are pinned here.
/// </summary>
public sealed class ComplianceParityTests
{
    private const string Module = @"C:\projects\collection-and-compliance-manager\Module\CCMCommon.psd1";

    private static readonly DateTime AsOf = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private const string Runner = """
        param([string]$ModulePath, [string]$CasesPath, [string]$OutPath)
        $ErrorActionPreference = 'Stop'
        Import-Module $ModulePath -Force -DisableNameChecking 3>$null 6>$null
        $cases = Get-Content -LiteralPath $CasesPath -Raw | ConvertFrom-Json
        $result = [ordered]@{}
        $result.Tags = @(foreach ($c in $cases.Tags) { Get-DriftTaggedDescription -Existing ([string]$c.Existing) -Id $c.Id -Version $c.Version })
        $result.States = @(foreach ($c in $cases.States) { Get-CcmDriftStatus -DeployedVersion ([string]$c.Deployed) -CatalogVersion ([string]$c.Catalog) })
        $global:FreshDates = @($cases.Fresh | ForEach-Object { [datetime]::Parse($_, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal) })
        function global:Get-CMBaseline { param([switch]$Fast) [pscustomobject]@{ LocalizedDisplayName = 'B' } }
        function global:Get-CMBaselineDeployment { param($InputObject) $i = 0; foreach ($d in $global:FreshDates) { [pscustomobject]@{ CollectionName = "C$i"; ModificationTime = $d }; $i++ } }
        $asOf = [datetime]::Parse($cases.AsOf, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
        $result.Stale = @(Get-EvaluationFreshness -AsOf $asOf | ForEach-Object { [bool]$_.IsStale })
        $result.Arguments = @(foreach ($c in $cases.Arguments) {
            $entry = [pscustomobject]@{ Builder = @{ Parameters = @($c.Parameters | ForEach-Object { @{ Name = $_.Name; Type = $_.Type } }) } }
            $values = @{}; foreach ($p in $c.Values.PSObject.Properties) { $values[$p.Name] = [string]$p.Value }
            $list = Get-CatalogBuilderArgumentList -Entry $entry -Values $values
            (@($list | ForEach-Object { $_ }) -join '|')
        })
        [IO.File]::WriteAllText($OutPath, (ConvertTo-Json -InputObject $result -Depth 6 -Compress), (New-Object System.Text.UTF8Encoding $false))
        """;

    private static readonly (string Existing, string Id, string Version)[] TagCases =
    [
        ("", "a", "1.0.0"), ("Desc", "a", "1.0.0"), ("Desc  ", "a", "1.0.0"), ("Desc [ccm:a@1.0.0]", "a", "1.1.0"),
        ("Desc [ccm:a@1.0.0] tail [ccm:b@2.0.0]", "a", "1.0.0"), ("[ccm:broken]", "a", "1.0.0"), ("x [ccm:a@1.0.0]", "a", "1.0.0")
    ];

    private static readonly (string Deployed, string Catalog)[] StateCases = [("", "1.0.0"), ("1.0.0", ""), ("1.0.0", "1.0.0"), ("1.0.0", "1.1.0"), ("2.0.0", "1.0.0")];

    private static readonly DateTime[] FreshCases = [AsOf.AddDays(-7), AsOf.AddDays(-7).AddSeconds(-1), AsOf.AddDays(-30), AsOf];

    private static readonly (string Entry, Dictionary<string, string> Values)[] ArgumentCases =
    [
        ("llmnr-disable", new() { ["SiteCode"] = "MCM", ["SiteServer"] = "cm01.contoso.com", ["CollectionName"] = "Pilot; one", ["DetectOnly"] = "True" }),
        ("llmnr-disable", new() { ["SiteCode"] = "MCM", ["SiteServer"] = "cm01.contoso.com", ["CollectionName"] = "", ["DetectOnly"] = "False" }),
        ("specexec-mitigations", new() { ["SiteCode"] = "MCM", ["SiteServer"] = "cm01.contoso.com", ["HtEnabledCollectionName"] = "A", ["HtDisabledCollectionName"] = "B", ["SkipDeploy"] = "1", ["DetectOnly"] = "True" }),
        ("do-downloadmode", new() { ["SiteCode"] = "MCM", ["SiteServer"] = "cm01.contoso.com", ["ClientCollectionName"] = "W", ["ServerCollectionName"] = "S", ["ClientDownloadMode"] = "0", ["ServerDownloadMode"] = "99", ["DetectOnly"] = "False" })
    ];

    /// <summary>The module's results for the cases above, recorded from CCMCommon on this repository's reference computer.</summary>
    private static readonly JsonElement Recorded = JsonDocument.Parse("""
        {"Tags":["[ccm:a@1.0.0]","Desc [ccm:a@1.0.0]","Desc [ccm:a@1.0.0]","Desc [ccm:a@1.1.0]","Desc tail [ccm:a@1.0.0]","[ccm:a@1.0.0]","x [ccm:a@1.0.0]"],
         "States":["Untagged","NotInCatalog","InSync","Drifted","Drifted"],
         "Stale":[false,true,true,false],
         "Arguments":["-SiteCode|MCM|-SiteServer|cm01.contoso.com|-CollectionName|Pilot; one|-DetectOnly","-SiteCode|MCM|-SiteServer|cm01.contoso.com",
                      "-SiteCode|MCM|-SiteServer|cm01.contoso.com|-HtEnabledCollectionName|A|-HtDisabledCollectionName|B|-SkipDeploy|-DetectOnly",
                      "-SiteCode|MCM|-SiteServer|cm01.contoso.com|-ClientCollectionName|W|-ServerCollectionName|S|-ClientDownloadMode|0|-ServerDownloadMode|99"]}
        """).RootElement;

    private static readonly Lazy<(JsonElement Results, bool Live)> Source = new(RunSource);

    private static (JsonElement, bool) RunSource()
    {
        if (!File.Exists(Module) || !File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))) return (Recorded, false);
        var catalog = new CatalogService();
        var folder = Path.Combine(Path.GetTempPath(), "apkg-compparity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cases = new
            {
                Tags = TagCases.Select(c => new { c.Existing, c.Id, c.Version }),
                States = StateCases.Select(c => new { c.Deployed, c.Catalog }),
                Fresh = FreshCases.Select(d => d.ToString("o")),
                AsOf = AsOf.ToString("o"),
                Arguments = ArgumentCases.Select(c => new { Parameters = catalog.Find(c.Entry)!.Parameters.Select(p => new { p.Name, p.Type }), Values = c.Values })
            };
            File.WriteAllText(Path.Combine(folder, "cases.json"), JsonSerializer.Serialize(cases));
            File.WriteAllText(Path.Combine(folder, "runner.ps1"), Runner, new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(folder, "runner.ps1"),
                         "-ModulePath", Module, "-CasesPath", Path.Combine(folder, "cases.json"), "-OutPath", Path.Combine(folder, "out.json") })
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "The source module did not finish.");
            Assert.True(File.Exists(Path.Combine(folder, "out.json")), "The source module wrote no result: " + error.Result);
            return (JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "out.json"))).RootElement.Clone(), true);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static List<string> Strings(string name) => Source.Value.Results.GetProperty(name).EnumerateArray().Select(e => e.GetString() ?? "").ToList();

    [Fact]
    public void TheRecordedResultsMatchTheModuleWhenItIsPresent()
    {
        if (!Source.Value.Live) return;
        foreach (var name in new[] { "Tags", "States", "Arguments" })
            Assert.Equal(Recorded.GetProperty(name).EnumerateArray().Select(e => e.GetString()), Strings(name));
        Assert.Equal(Recorded.GetProperty("Stale").EnumerateArray().Select(e => e.GetBoolean()), Source.Value.Results.GetProperty("Stale").EnumerateArray().Select(e => e.GetBoolean()));
    }

    [Fact]
    public void TheDriftTagIsWrittenAsTheModuleWritesIt()
    {
        var expected = Strings("Tags");
        Assert.Equal(TagCases.Length, expected.Count);
        for (var i = 0; i < TagCases.Length; i++)
            Assert.Equal(expected[i], DriftTag.Apply(TagCases[i].Existing, TagCases[i].Id, TagCases[i].Version));
    }

    [Fact]
    public void TheDriftStateMapsToTheModuleStatusWithNewerCatalogVersionsUnknown()
    {
        var expected = Strings("States");
        Assert.Equal(StateCases.Length, expected.Count);
        var map = new Dictionary<string, string[]> { ["Untagged"] = ["Untagged"], ["NotInCatalog"] = ["Unknown"], ["InSync"] = ["Current"], ["Drifted"] = ["Behind", "Unknown"] };
        var entry = new CatalogService().Find("llmnr-disable")!;
        for (var i = 0; i < StateCases.Length; i++)
        {
            var (deployed, catalog) = StateCases[i];
            var description = deployed.Length == 0 ? "x" : $"x [ccm:llmnr-disable@{deployed}]";
            var ours = DriftTag.State(description, catalog.Length == 0 ? null : entry with { Version = catalog });
            Assert.Contains(ours, map[expected[i]]);
        }
        // By design: a deployed version newer than the catalog is Unknown here; the module calls it Drifted.
        Assert.Equal("Unknown", DriftTag.State("x [ccm:llmnr-disable@2.0.0]", entry));
        Assert.Equal("Behind", DriftTag.State("x [ccm:llmnr-disable@0.9.0]", entry));
    }

    [Fact]
    public void TheStaleRuleMatchesTheModuleAtTheBoundary()
    {
        var expected = Source.Value.Results.GetProperty("Stale").EnumerateArray().Select(e => e.GetBoolean()).ToList();
        Assert.Equal(FreshCases.Length, expected.Count);
        Assert.Equal(expected, FreshCases.Select(d => ComplianceService.IsStale(d, AsOf)));
    }

    [Fact]
    public void BuilderArgumentsMatchTheModuleForFullySpecifiedValues()
    {
        var expected = Strings("Arguments");
        Assert.Equal(ArgumentCases.Length, expected.Count);
        var catalog = new CatalogService();
        for (var i = 0; i < ArgumentCases.Length; i++)
        {
            var (entry, values) = ArgumentCases[i];
            var (arguments, errors) = CatalogArguments.Build(catalog.Find(entry)!, values);
            Assert.Empty(errors);
            Assert.Equal(expected[i], string.Join("|", arguments));
        }
    }

    [Fact]
    public void DifferencesByDesignArePinned()
    {
        var llmnr = new CatalogService().Find("llmnr-disable")!;
        // DetectOnly is on unless the operator turns it off; the module passes no switch for a missing value.
        Assert.Contains("-DetectOnly", CatalogArguments.Build(llmnr, new Dictionary<string, string> { ["SiteCode"] = "MCM", ["SiteServer"] = "cm01" }).Arguments);
        // A value the module would pass but Windows PowerShell would read as a parameter is refused here.
        Assert.NotEmpty(CatalogArguments.Build(llmnr, new Dictionary<string, string> { ["SiteCode"] = "MCM", ["SiteServer"] = "cm01", ["CollectionName"] = "-DetectOnly" }).Errors);
        // A tag that is not of the catalog form is not read as a catalog ID.
        Assert.Null(new BaselineRow("1", "B", "x [ccm:Bad Id@v1]", 1, null, null, 0, 0, null).CatalogId);
        Assert.Equal("Unknown", DriftTag.State("x [ccm:Bad Id@v1]", llmnr));
    }
}
