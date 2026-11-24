using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The names of this app are the names of the PowerShell app (App Packager), for every packager. ConfigMgr already holds the
/// applications that the PowerShell app made, and Check ConfigMgr, Discover, Convert from ConfigMgr and Import from App Packager
/// (PowerShell) find them by these names. The PowerShell side is the golden file <c>Fixtures/TitleParity/PowerShellTitles.golden.json</c>,
/// made from the packager scripts by <c>tools/title-parity/Export-PowerShellTitles.ps1</c>. After a change of a packager in the
/// PowerShell app, run that script again and then <c>tools/title-parity/Sync-DefinitionTitles.ps1</c>.
/// A name that the PowerShell app gets wrong is listed under Defects in <c>known-differences.json</c>, with the reason, and is the only way
/// that a name may differ.
/// </summary>
public sealed class TitleParityTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record GoldenPackager(string Packager, string Definition, string Vendor, string App, string CMName, string StageKind, string[] StageNames);
    private sealed record Golden(int Count, string ArchitectureTagPattern, bool AppendArchitectureDefault, GoldenPackager[] Packagers);
    private sealed record Known(KnownEntry[] PowerShellOnly, KnownEntry[] ApdOnly, KnownEntry[] Defects);
    private sealed record KnownEntry(string? Packager, string? Definition, string? Field, string Reason);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "TitleParity", name);
    private static Golden LoadGolden() => JsonSerializer.Deserialize<Golden>(File.ReadAllText(Fixture("PowerShellTitles.golden.json")), Json)!;
    private static Known LoadKnown() => JsonSerializer.Deserialize<Known>(File.ReadAllText(Fixture("known-differences.json")), Json)!;

    private static Dictionary<string, AppDefinition> Definitions() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(f), Json)!, StringComparer.OrdinalIgnoreCase);

    private static bool IsDefect(Known known, string packager, string field) =>
        known.Defects.Any(d => string.Equals(d.Packager, packager, StringComparison.OrdinalIgnoreCase) && (d.Field is null || string.Equals(d.Field, field, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void TheGoldenFileCoversTheCatalogOfThePowerShellApp()
    {
        var golden = LoadGolden();
        Assert.True(golden.Packagers.Length >= 300, golden.Packagers.Length.ToString());
        Assert.Equal(golden.Count, golden.Packagers.Length);
        Assert.All(golden.Packagers, p => Assert.False(string.IsNullOrWhiteSpace(p.App), $"{p.Packager} has no App header"));
    }

    [Fact]
    public void EveryPackagerHasADefinitionOrIsListedAsPowerShellOnly()
    {
        var golden = LoadGolden();
        var known = LoadKnown();
        var missing = golden.Packagers.Where(p => p.Definition.Length == 0 && !known.PowerShellOnly.Any(k => string.Equals(k.Packager, p.Packager, StringComparison.OrdinalIgnoreCase))).Select(p => p.Packager).ToList();
        Assert.True(missing.Count == 0, "No definition for: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryDefinitionBelongsToAPowerShellPackagerOrIsListedAsApdOnly()
    {
        var golden = LoadGolden();
        var known = LoadKnown();
        var stray = Definitions().Keys.Where(d => !golden.Packagers.Any(p => string.Equals(p.Definition, d, StringComparison.OrdinalIgnoreCase))
                                                  && !known.ApdOnly.Any(k => string.Equals(k.Definition, d, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(stray.Count == 0, "No PowerShell packager and not listed as APD only: " + string.Join(", ", stray));
    }

    [Fact]
    public void TheGridTitleOfEveryDefinitionIsTheTitleOfThePowerShellApp()
    {
        var known = LoadKnown();
        var definitions = Definitions();
        var failures = new List<string>();
        foreach (var p in LoadGolden().Packagers.Where(p => p.Definition.Length > 0))
        {
            var row = PackagerDiscoveryService.DefinitionRow(definitions[p.Definition], p.Definition + ".json");
            if (row.Application != p.App && !IsDefect(known, p.Packager, "Application")) failures.Add($"{p.Packager}: PowerShell '{p.App}', APD grid '{row.Application}'");
        }
        Assert.True(failures.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void TheConfigMgrNameOfEveryDefinitionIsTheNameOfThePowerShellApp()
    {
        var known = LoadKnown();
        var definitions = Definitions();
        var failures = new List<string>();
        foreach (var p in LoadGolden().Packagers.Where(p => p.Definition.Length > 0))
        {
            var row = PackagerDiscoveryService.DefinitionRow(definitions[p.Definition], p.Definition + ".json");
            if (row.CMName != p.CMName && !IsDefect(known, p.Packager, "CMName")) failures.Add($"{p.Packager}: PowerShell CMName '{p.CMName}', APD '{row.CMName}'");
        }
        Assert.True(failures.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ASearchFindsTheSameRowsAsInThePowerShellApp()
    {
        var definitions = Definitions();
        var rows = LoadGolden().Packagers.Where(p => p.Definition.Length > 0)
            .Select(p => (Packager: p, Row: PackagerDiscoveryService.DefinitionRow(definitions[p.Definition], p.Definition + ".json"))).ToList();
        // The search of the PowerShell app: the text, in lower case, anywhere in Application, Vendor, Status or CMName.
        foreach (var term in new[] { "putty", "PUTTY", "(x64)", "7-zip", "git", "temurin", "ready", "microsoft", "x86" })
        {
            var expected = rows.Where(r => new[] { r.Packager.App, r.Packager.Vendor, r.Row.Status, r.Packager.CMName }
                .Any(v => (v ?? "").ToLowerInvariant().Contains(term.ToLowerInvariant()))).Select(r => r.Packager.Packager).Order().ToList();
            var actual = rows.Where(r => PackagerSearch.Matches(r.Row, term)).Select(r => r.Packager.Packager).Order().ToList();
            Assert.Equal(expected, actual);
        }
        Assert.Contains(rows, r => r.Packager.Packager == "putty" && PackagerSearch.Matches(r.Row, "PuTTY"));
    }

    [Fact]
    public void ADefinitionThatThePowerShellAppNamesWithAFixedNameStagesThatName()
    {
        var known = LoadKnown();
        var definitions = Definitions();
        var failures = new List<string>();
        foreach (var p in LoadGolden().Packagers.Where(p => p.Definition.Length > 0 && p.StageKind == "template" && p.StageNames.Length == 1))
        {
            var def = definitions[p.Definition];
            var identity = StagingService.ResolveStageIdentity(def, "1.2.3", def.Vendor, null);
            var expected = p.StageNames[0].Replace("{version}", "1.2.3", StringComparison.OrdinalIgnoreCase);
            if (identity.AppName != expected && !IsDefect(known, p.Packager, "StageName")) failures.Add($"{p.Packager}: PowerShell stage name '{expected}', APD '{identity.AppName}'");
        }
        Assert.True(failures.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EveryApplicationThatThePowerShellAppCreatesIsFoundByTheCMNameOfTheDefinition()
    {
        // Check ConfigMgr, Discover and the conversion find an existing application by CMName. The titles are those the PowerShell app
        // creates for each packager with a fixed name: the name itself and the name with the version appended (IncludeVersion).
        var definitions = Definitions();
        var failures = new List<string>();
        foreach (var p in LoadGolden().Packagers.Where(p => p.Definition.Length > 0 && p.StageKind == "template"))
        {
            var def = definitions[p.Definition];
            var major = Regex.Matches(def.CMName, @"\d+").LastOrDefault()?.Value ?? "1";
            var version = $"{major}.2.3";
            foreach (var stageName in p.StageNames)
            {
                var title = stageName.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);
                // IncludeVersion appends the version only when the name has none (Get-PackagedApplicationName).
                foreach (var created in title.Contains(version) ? new[] { title } : new[] { title, $"{title} - {version}" })
                    if (!MecmApplicationTitleRule.IsManaged(def.CMName, [created])) failures.Add($"{p.Packager}: CMName '{def.CMName}' does not find '{created}'");
            }
        }
        Assert.True(failures.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("putty", "PuTTY release 0.83 (64-bit)")]
    [InlineData("awssamcli", "AWS SAM Command Line Interface")]
    [InlineData("intunedebugtoolkit", "IntuneDebugTools")]
    public void TheCMNameOfAPackagerNamedByItsInstallerFindsTheApplicationThatTheInstallerNames(string definition, string titleOnTheSite)
    {
        var def = Definitions()[definition];

        Assert.True(MecmApplicationTitleRule.IsManaged(def.CMName, [titleOnTheSite]), $"'{def.CMName}' does not find '{titleOnTheSite}'");
    }

    [Theory]
    [InlineData("7zip", "7-Zip 26.06 (x64 edition)")]
    [InlineData("7zip", "7-Zip")]
    [InlineData("git", "Git 2.47.1")]
    [InlineData("notepadplusplus", "Notepad++ 8.8.1")]
    public void TheApplicationsOfSomeWellKnownPackagersAreFound(string definition, string titleOnTheSite)
    {
        var def = Definitions()[definition];
        var golden = LoadGolden().Packagers.Single(p => p.Definition == definition);

        Assert.Equal(golden.CMName, def.CMName);
        Assert.True(MecmApplicationTitleRule.IsManaged(def.CMName, [titleOnTheSite]), $"'{def.CMName}' does not find '{titleOnTheSite}'");
    }

    [Fact]
    public void ADefinitionOfAPackagerThatReadsTheNameFromTheInstallerDoesNotFixAName()
    {
        // The PowerShell packager takes the name of the application from the installer (MSI ProductName and the like). APD does the same
        // when the definition fixes no name; a definition that fixes one must be a name the PowerShell app also builds from a version.
        var definitions = Definitions();
        var fixedNames = LoadGolden().Packagers.Where(p => p.Definition.Length > 0 && p.StageKind == "installer" && !string.IsNullOrWhiteSpace(definitions[p.Definition].AppName))
            .Select(p => p.Packager).Order().ToList();
        Assert.Equal(["msvcruntimesx64", "msvcruntimesx86"], fixedNames);
    }

    [Fact]
    public void TheArchitectureRuleAndItsDefaultAreThoseOfThePowerShellApp()
    {
        var golden = LoadGolden();
        Assert.Equal(golden.AppendArchitectureDefault, new AppSettings().AppendArchitectureToTitle);

        var tag = new Regex(golden.ArchitectureTagPattern, RegexOptions.IgnoreCase);
        // The PowerShell function: a repeated tag becomes one, and with the option off a trailing tag goes.
        string PowerShell(string title, bool append)
        {
            var result = Regex.Replace(title, "(" + golden.ArchitectureTagPattern + @")(\s+\1)+", "$1", RegexOptions.IgnoreCase);
            if (!append) result = Regex.Replace(result, @"\s*" + golden.ArchitectureTagPattern + @"\s*$", "", RegexOptions.IgnoreCase);
            return result.Trim();
        }

        var titles = new List<string>();
        foreach (var p in golden.Packagers)
        {
            titles.Add(p.App);
            titles.Add(p.CMName);
            titles.AddRange(p.StageNames.Select(n => n.Replace("{version}", "1.2.3")));
        }
        titles.AddRange(["Tool (x64) (x64)", "Tool (X64) (x64)", "Tool (x64) - 1.2", "Tool (x86) (ARM64)", "Tool (64-bit)"]);
        Assert.Contains(titles, t => tag.IsMatch(t));
        foreach (var title in titles.Distinct())
        {
            Assert.Equal(PowerShell(title, true), TitleNaming.Finish(title, true));
            Assert.Equal(PowerShell(title, false), TitleNaming.Finish(title, false));
        }
    }

    [Fact]
    public void TheTitleOfEveryDefinitionIsTheSameWithEachSettingOfTheArchitectureOptionExceptForATrailingTag()
    {
        // With the default setting (tag added) the grid title is shown and stored as it is. With the option off, only a trailing tag goes,
        // and a title without a tag is the same under both settings (PuTTY, 7-Zip, Notepad++).
        var definitions = Definitions();
        foreach (var p in LoadGolden().Packagers.Where(p => p.Definition.Length > 0))
        {
            var withTag = TitleNaming.Finish(p.App, true);
            var withoutTag = TitleNaming.Finish(p.App, false);
            Assert.Equal(p.App, withTag);
            if (withTag != withoutTag) Assert.EndsWith(")", p.App);
        }
        Assert.Equal("PuTTY", TitleNaming.Finish(definitions["putty"].Application, true));
        Assert.Equal("PuTTY", TitleNaming.Finish(definitions["putty"].Application, false));
    }

    [Fact]
    public void ADefinitionKeepsTheStageFolderOfBeforeTheTitlesFollowedThePowerShellApp()
    {
        // The folder of a stage does not move when the title changes, so a stage that exists is found again.
        var changed = Definitions().Values.Where(d => d.StageFolderName is not null).ToList();
        Assert.True(changed.Count > 100, changed.Count.ToString());
        Assert.All(changed, d => Assert.Equal($"{d.StageFolderName!.Replace(" ", "")}-{d.Architecture}", StageFolder.Name(d)));
    }
}
