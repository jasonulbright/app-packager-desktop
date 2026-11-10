using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// The WsusSupport value of a definition: its grammar, its presence in every shipped
/// definition, and its agreement with the publisher's own compatibility check on the
/// default build.
/// </summary>
public sealed class WsusSupportTagTests
{
    private static readonly string[] BlockingCodes =
    [
        WsusRefusalCodes.MultipleDeploymentTypes, WsusRefusalCodes.InstallerTypeUnsupported, WsusRefusalCodes.PerUserInstall,
        WsusRefusalCodes.InteractiveInstall, WsusRefusalCodes.CustomInstall, WsusRefusalCodes.PayloadOutsideStage,
        WsusRefusalCodes.InstallerMissing, WsusRefusalCodes.InstallerInSubfolder, WsusRefusalCodes.DetectionNotMappable,
        WsusRefusalCodes.PayloadInSubfolder, WsusRefusalCodes.PayloadMissing, WsusRefusalCodes.PayloadTooLarge,
        WsusRefusalCodes.ExitCodes
    ];

    [Fact]
    public void DellCommandUpdateDeclaresTheExitCodesRefusalBecauseItReportsARebootCodeOfItsOwn()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Definitions", "dellcommandupdate.json");
        var def = System.Text.Json.JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal([2], def.RebootExitCodes);
        Assert.Equal("No (ExitCodes)", def.WsusSupport);
        Assert.Equal("ExitCodes", WsusSupportTag.NotSupportedCodes(def.WsusSupport));
        // The row of the grid and of the One Click plan carries the refusal, so the plan shows it before a run.
        Assert.Equal("ExitCodes", PackagerDiscoveryService.DefinitionRow(def, "dellcommandupdate.json").WsusNotSupported);
    }

    private static IEnumerable<string> DefinitionFiles() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(path => Path.GetFileName(path)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

    public static TheoryData<string> Definitions()
    {
        var data = new TheoryData<string>();
        foreach (var name in DefinitionFiles()) data.Add(name);
        return data;
    }

    [Theory]
    [InlineData("Yes", true, "")]
    [InlineData("No (PerUserInstall)", false, "PerUserInstall")]
    [InlineData("No (CustomInstall|PayloadTooLarge)", false, "CustomInstall|PayloadTooLarge")]
    [InlineData("  No ( DetectionNotMappable )  ", false, "DetectionNotMappable")]
    [InlineData("No", false, "")]
    public void ADeclaredValueReadsAsSupportedOrAsItsRefusalCodes(string text, bool supported, string codes)
    {
        var tag = WsusSupportTag.Parse(text);

        Assert.NotNull(tag);
        Assert.Equal(supported, tag.Supported);
        Assert.Equal(codes, tag.CodeText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Maybe")]
    [InlineData("Yes (CustomInstall)")]
    [InlineData("No (Custom Install)")]
    [InlineData("yes")]
    public void AnyOtherTextDeclaresNothing(string? text)
    {
        Assert.Null(WsusSupportTag.Parse(text));
        Assert.Equal("", WsusSupportTag.NotSupportedCodes(text));
    }

    [Fact]
    public void TheTextOfATagRoundTrips()
    {
        Assert.Equal("Yes", WsusSupportTag.FromBlockingCodes([]).Text);
        Assert.Equal("No (CustomInstall|InstallerMissing)", WsusSupportTag.FromBlockingCodes(["CustomInstall", "InstallerMissing", "CustomInstall"]).Text);
        Assert.Equal("CustomInstall|InstallerMissing", WsusSupportTag.NotSupportedCodes("No (CustomInstall|InstallerMissing)"));
        Assert.Equal("", WsusSupportTag.NotSupportedCodes("Yes"));
        Assert.Equal("declared", WsusSupportTag.NotSupportedCodes("No"));
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void EveryShippedDefinitionDeclaresAValidValueOnTheLineAfterItsId(string fileName)
    {
        var def = DefinitionFixture.Load(fileName);
        var tag = WsusSupportTag.Parse(def.WsusSupport);

        Assert.True(tag is not null, $"{fileName} declares '{def.WsusSupport}'.");
        if (tag!.Supported) Assert.Empty(tag.Codes);
        else
        {
            Assert.NotEmpty(tag.Codes);
            Assert.All(tag.Codes, code => Assert.Contains(code, BlockingCodes));
        }
        Assert.Equal(tag.Text, def.WsusSupport);

        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Definitions", fileName));
        var id = Array.FindIndex(lines, l => l.TrimStart().StartsWith("\"Id\"", StringComparison.Ordinal));
        Assert.True(id >= 0 && lines[id + 1].TrimStart().StartsWith("\"WsusSupport\"", StringComparison.Ordinal), fileName);
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public void TheDeclaredValueIsWhatTheCompatibilityCheckGivesForTheDefaultBuild(string fileName)
    {
        var def = DefinitionFixture.Load(fileName);
        var possible = DefaultBuildWsusCheck.Possible(def);

        if (possible.Count == 1)
        {
            Assert.True(possible[0] == def.WsusSupport, $"{fileName} declares '{def.WsusSupport}'; the check gives '{possible[0]}'.");
            Assert.False(WsusSupportPins.Expected.ContainsKey(fileName), $"{fileName} has one result and needs no recorded value.");
            return;
        }

        // Two results: the stand-in installer cannot decide, the recorded result of the real MSI does.
        Assert.True(WsusSupportPins.Expected.TryGetValue(fileName, out var recorded),
            $"{fileName} depends on its staged MSI; record its result in {nameof(WsusSupportPins)}.");
        Assert.Contains(recorded, possible);
        Assert.True(recorded == def.WsusSupport, $"{fileName} declares '{def.WsusSupport}'; the recorded result of its MSI is '{recorded}'.");
    }

    [Fact]
    public void EveryRecordedResultBelongsToAShippedDefinition() =>
        Assert.Empty(WsusSupportPins.Expected.Keys.Except(DefinitionFiles(), StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void TheCheckHasTwoResultsOnlyWhereItReadsTheStagedMsi()
    {
        var twoResults = DefinitionFiles().Select(DefinitionFixture.Load)
            .Where(def => DefaultBuildWsusCheck.Possible(def).Count > 1).ToList();

        Assert.NotEmpty(twoResults);
        Assert.All(twoResults, def =>
        {
            Assert.Equal("MSI", def.InstallerType, ignoreCase: true);
            Assert.Equal(["Yes", "No (DetectionNotMappable)"], DefaultBuildWsusCheck.Possible(def));
        });
    }

    [Fact]
    public void TheGridRowOfADefinitionCarriesItsDeclaredRefusal()
    {
        var refused = PackagerDiscoveryService.DefinitionRow(DefinitionFixture.Load("ccleaner.json"), "ccleaner.json");
        var supported = PackagerDiscoveryService.DefinitionRow(DefinitionFixture.Load("7zip.json"), "7zip.json");
        var silent = PackagerDiscoveryService.DefinitionRow(new AppDefinition { Id = "x", Application = "X" }, "x.json");

        Assert.Equal("CustomInstall|ExitCodes", refused.WsusNotSupported);
        Assert.Equal("", supported.WsusNotSupported);
        Assert.Equal("", silent.WsusNotSupported);
    }
}
