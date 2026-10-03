using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// The C# rule builder against the PowerShell publisher's own output for the
/// same manifests. Fixtures\WsusRules\upstream-rules.json is what
/// Get-UpstreamWsusRules.ps1 printed under Windows PowerShell 5.1 for every
/// case in cases.json; the rule text and the error text must match exactly.
/// The file rules carry this computer's known folders, so the fixture holds
/// only for a computer with the standard Program Files, ProgramData and
/// Windows locations on drive C.
/// </summary>
public class WsusRuleFixtureTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WsusRules");
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var node in Cases()) data.Add(node!["Name"]!.GetValue<string>());
        return data;
    }

    private static JsonArray Cases() => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "cases.json")))!.AsArray();

    private static JsonObject Upstream(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "upstream-rules.json")))!.AsArray()
            .OfType<JsonObject>().Single(n => n["Name"]!.GetValue<string>() == name);

    [Fact]
    public void EveryCaseHasAnUpstreamResult()
    {
        var upstream = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "upstream-rules.json")))!.AsArray()
            .Select(n => n!["Name"]!.GetValue<string>()).ToList();
        Assert.Equal(Cases().Select(n => n!["Name"]!.GetValue<string>()).ToList(), upstream);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void TheRulesMatchThePowerShellPublisher(string name)
    {
        var testCase = Cases().OfType<JsonObject>().Single(n => n["Name"]!.GetValue<string>() == name);
        var manifest = testCase["Manifest"]!.Deserialize<StageManifest>(Options)!;
        var content = testCase["ContentFolder"]?.GetValue<bool>() == true ? Folder : null;
        var expected = Upstream(name);
        var builder = new WsusRuleBuilder(new MsiInspectionService());

        if (expected["Error"] is { } error)
        {
            var ex = Assert.Throws<WsusRuleException>(() => builder.Build(manifest, content));
            Assert.Equal(error.GetValue<string>(), ex.Message);
            return;
        }

        var rules = builder.Build(manifest, content);
        Assert.Equal(expected["IsInstalled"]!.GetValue<string>(), rules.IsInstalled);
        Assert.Equal(expected["IsInstallable"]!.GetValue<string>(), rules.IsInstallable);
        Assert.Equal(expected["Source"]!.GetValue<string>(), rules.Source.ToString());
        if (expected["Entry"] is JsonObject entry)
        {
            Assert.Equal(entry["View"]!.GetValue<string>(), rules.Entry!.View);
            Assert.Equal(entry["Publisher"]!.GetValue<string>(), rules.Entry.Publisher);
            Assert.Equal(entry["Version"]!.GetValue<string>(), rules.Entry.Version);
            Assert.Equal(entry["WindowsInstaller"]!.GetValue<string>(), rules.Entry.WindowsInstaller ? "True" : "False");
            Assert.Equal(entry["DisplayNamePrefix"]?.GetValue<string>(), rules.Entry.DisplayNamePrefix);
            Assert.Equal(entry["DisplayNameSuffix"]?.GetValue<string>(), rules.Entry.DisplayNameSuffix);
            Assert.Equal(entry["DisplayName"]?.GetValue<string>(), rules.Entry.DisplayName);
        }
    }
}
