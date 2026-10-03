using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class WorkbenchFieldMappingTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void TheDetectionRule_RoundTripsThroughItsStoredTokens()
    {
        var rule = new WorkbenchDetectionRule
        {
            Type = "PowerShell", View = "32-bit", VersionBinding = "Pinned", Logic = "Two groups",
            Key = @"SOFTWARE\Contoso", ScriptText = "Write-Output 'x'",
            Clauses = [new WorkbenchDetectionClause("1", "Registry", "HKLM\\x", "Exists", "")]
        };

        var stored = rule.ToJson();

        Assert.Equal("Script", stored["Type"]!.GetValue<string>());
        Assert.Equal("32", stored["View"]!.GetValue<string>());
        Assert.Equal("Pinned", stored["VersionBinding"]!.GetValue<string>());
        Assert.Equal("TwoGroup", stored["Logic"]!.GetValue<string>());
        var back = WorkbenchDetectionRule.FromJson(stored);
        Assert.Equal(rule with { Clauses = back.Clauses }, back);
        Assert.Equal(rule.Clauses, back.Clauses);
        var defaults = new WorkbenchDetectionRule();
        Assert.Equal(defaults, WorkbenchDetectionRule.FromJson(null) with { Clauses = defaults.Clauses });
    }

    [Fact]
    public void RequirementRows_StoreCulturesAsAListAndTheVpnValueAsABoolean()
    {
        var language = new WorkbenchRequirementRow("Add", "os-language", "os-language", "de-DE, fr-FR", "*").ToJson();
        var vpn = new WorkbenchRequirementRow("Replace", "vpn-connected", "vpn-connected", "false", "Online, Managed").ToJson();
        var arch = WorkbenchRequirementRow.ForTemplate("cpu-arch") with { Value = "ARM64" };

        Assert.Equal(["de-DE", "fr-FR"], language["Rule"]!["Cultures"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(vpn["Rule"]!["Value"]!.GetValue<bool>());
        Assert.Equal(["Online", "Managed"], vpn["AppliesTo"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("ARM64", arch.ToJson()["Rule"]!["Value"]!.GetValue<string>());

        Assert.Equal(new WorkbenchRequirementRow("Add", "os-language", "os-language", "de-DE, fr-FR", "*"), WorkbenchRequirementRow.FromJson(language));
        Assert.Equal("False", WorkbenchRequirementRow.FromJson(vpn).Value);

        var profile = WorkbenchProfile.CreateNew("catalog:package-req", name: "x");
        profile.Set("Requirements.Operations", new JsonArray(language, vpn));
        WorkbenchProfileValidator.Validate(profile.Data);
    }

    [Fact]
    public void TheMigratedRequirementShape_ReadsIntoTheGrid()
    {
        var migrated = TestJson("""{"Op":"Add","RuleId":"vpn-connected","Rule":{"ConditionId":"vpn-connected","Value":true},"AppliesTo":["*"]}""");

        Assert.Equal(new WorkbenchRequirementRow("Add", "vpn-connected", "vpn-connected", "True", "*"), WorkbenchRequirementRow.FromJson(migrated));
    }

    [Fact]
    public void VariantSplit_ReadsTheMigratedDocumentAnArrayOrNothing()
    {
        Assert.Equal("Language", WorkbenchFieldMapping.VariantSplitToDisplay(TestJson("""{"SchemaVersion":1,"Split":"Language"}""")));
        Assert.Equal("Network", WorkbenchFieldMapping.VariantSplitToDisplay(new JsonArray("Network")));
        Assert.Equal("None", WorkbenchFieldMapping.VariantSplitToDisplay(new JsonArray()));
        Assert.Equal("None", WorkbenchFieldMapping.VariantSplitToDisplay(null));

        var written = Assert.IsType<JsonObject>(WorkbenchFieldMapping.DisplayToVariantSplit("Architecture"));
        Assert.Equal("Architecture", written["Split"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonArray>(WorkbenchFieldMapping.DisplayToVariantSplit("None")));
    }

    [Fact]
    public void ComboTextAndStoredTokens_Convert()
    {
        Assert.Equal("Include version", WorkbenchFieldMapping.TitleModeToDisplay("IncludeVersion"));
        Assert.Equal("No version", WorkbenchFieldMapping.TitleModeToDisplay("No version"));
        Assert.Null(WorkbenchFieldMapping.DisplayToTitleMode("Packager default"));
        Assert.Equal("Extend", WorkbenchFieldMapping.DisplayToScriptMode("Extend generated"));
        Assert.Null(WorkbenchFieldMapping.DisplayToScriptMode("Generated"));
        Assert.Equal(["Packager default", "System", "User"], WorkbenchFieldMapping.InstallForOptions(["CurrentUser", "AllUsers"]));
        Assert.Equal(["Packager default"], WorkbenchFieldMapping.InstallForOptions(null));
        Assert.Equal("CurrentUser", WorkbenchFieldMapping.DisplayToInstallMode("User"));
    }

    [Fact]
    public void TheGutter_CarriesOneNumberPerLine()
    {
        Assert.Equal("1", WorkbenchFieldMapping.LineNumbers(""));
        var gutter = WorkbenchFieldMapping.LineNumbers("Write-Output 'one'\nWrite-Output 'two'\r\nWrite-Output 'three'");
        Assert.Equal(["1", "2", "3"], gutter.Split(Environment.NewLine));
    }

    [Fact]
    public void SourceFiles_FromAFolderKeepTheFolderNameInTheDestination_AndReplaceKeepsTheDestination()
    {
        var folder = _t.NewFolder("payload");
        _t.NewFile(Path.Combine(folder, "a.txt"), "aaa");
        _t.NewFile(Path.Combine(folder, "sub", "b.txt"), "bb");
        var other = _t.NewFile(Path.Combine(_t.NewFolder(), "c.txt"), "c");

        var entries = WorkbenchSourceFiles.FromPath(folder);

        Assert.Equal([@"payload\a.txt", @"payload\sub\b.txt"], entries.Select(e => e.Destination));
        Assert.Equal(3, entries[0].Size);
        Assert.Equal(64, entries[0].Sha256.Length);
        Assert.Equal(entries[0].Sha256[..16], entries[0].ShortHash);
        Assert.All(entries, e => Assert.False(e.Linked));

        var replaced = WorkbenchSourceFiles.Replace(entries[0] with { Asset = "0123456789abcdef", Linked = true }, other);
        Assert.Equal(@"payload\a.txt", replaced.Destination);
        Assert.Equal("", replaced.Asset);
        Assert.True(replaced.Linked);
        Assert.Equal(Path.GetFullPath(other), replaced.Provenance);

        var json = WorkbenchSourceFiles.ToJson(entries);
        Assert.Equal(entries, WorkbenchSourceFiles.Read(json));
        Assert.Empty(WorkbenchSourceFiles.FromPath(Path.Combine(_t.Root, "missing")));
    }

    private static JsonNode TestJson(string json) => WorkbenchJson.Parse(json)!;
}
