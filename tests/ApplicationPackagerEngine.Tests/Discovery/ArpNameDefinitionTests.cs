using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Discovery;

public class ArpNameDefinitionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static List<(string File, AppDefinition Definition, string[] Lines)> Definitions() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(path => (Path.GetFileName(path), JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path), JsonOptions)!, File.ReadAllLines(path)))
            .ToList();

    [Fact]
    public void EveryShippedDefinitionHasAValidArpName()
    {
        var bad = new List<string>();
        foreach (var (file, definition, _) in Definitions())
        {
            var arpName = definition.ArpName;
            if (string.IsNullOrWhiteSpace(arpName) || arpName != arpName.Trim()) { bad.Add($"{file}: ArpName missing or padded"); continue; }
            if (ArpWildcard.IsNone(arpName))
            {
                if (arpName != "none") bad.Add($"{file}: write none in lower case");
                if (definition.ArpVersion is not null || definition.ArpArch is not null) bad.Add($"{file}: none with ArpVersion or ArpArch");
                continue;
            }
            foreach (var part in arpName.Split('|'))
            {
                var pattern = part.Trim();
                if (pattern.Length == 0) { bad.Add($"{file}: empty pattern"); continue; }
                if (pattern.All(c => c is '*' or '?' || char.IsWhiteSpace(c))) bad.Add($"{file}: bare wildcard '{pattern}'");
                if (string.Equals(pattern, "none", StringComparison.OrdinalIgnoreCase)) bad.Add($"{file}: none inside a pattern list");
                if (ArpWildcard.LiteralPrefixLength(pattern) < 3) bad.Add($"{file}: fewer than three literal characters before the first wildcard in '{pattern}'");
                if (!ArpWildcard.TryCreate(pattern, out _)) bad.Add($"{file}: not a valid wildcard '{pattern}'");
            }
            if (definition.ArpArch is { } arch && arch is not ("x64" or "x86")) bad.Add($"{file}: ArpArch '{arch}'");
            if (definition.ArpVersion is { } version && (version.Trim().Length == 0 || !ArpWildcard.TryCreate(version, out _)))
                bad.Add($"{file}: ArpVersion '{version}'");
        }
        Assert.True(bad.Count == 0, string.Join(Environment.NewLine, bad));
    }

    [Fact]
    public void NoTwoDefinitionsShareTheSameEffectiveMatchKey()
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (file, definition, _) in Definitions())
        {
            if (ArpWildcard.IsNone(definition.ArpName)) continue;
            var patterns = string.Join("|", definition.ArpName!.Split('|')
                .Select(p => p.Trim().ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));
            var key = $"{patterns} / {definition.ArpVersion?.Trim().ToLowerInvariant()} / {definition.ArpArch?.Trim().ToLowerInvariant()}";
            if (!owners.TryGetValue(key, out var list)) owners[key] = list = [];
            list.Add(file);
        }
        var shared = owners.Where(o => o.Value.Count > 1).Select(o => $"{o.Key} => {string.Join(", ", o.Value)}").ToList();
        Assert.True(shared.Count == 0, string.Join(Environment.NewLine, shared));
    }

    [Fact]
    public void TheArpFieldsFollowTheCpeLineAndLeaveTheFirstFiveLinesAlone()
    {
        foreach (var (file, _, lines) in Definitions())
        {
            var arpLine = Array.FindIndex(lines, l => l.TrimStart().StartsWith("\"ArpName\"", StringComparison.Ordinal));
            Assert.True(arpLine >= 5, $"{file}: ArpName line {arpLine + 1}");
            var cpeLine = Array.FindIndex(lines, l => l.TrimStart().StartsWith("\"Cpe\"", StringComparison.Ordinal));
            if (cpeLine >= 0) Assert.True(arpLine == cpeLine + 1, $"{file}: ArpName is not on the line after Cpe");
        }
    }

    [Theory]
    [InlineData("citrixworkspace-ltsr-x64.json")]
    [InlineData("citrixworkspace-ltsr-x86.json")]
    [InlineData("citrixworkspace-ltsr-arm64.json")]
    [InlineData("m365apps-x86.json")]
    [InlineData("m365project-x86.json")]
    [InlineData("m365visio-x86.json")]
    public void AVariantThatInventoryCannotSeparateIsNone(string file) =>
        Assert.Equal("none", DefinitionFixture.Load(file).ArpName);

    [Theory]
    [InlineData("dotnet8-x64.json", "Microsoft Windows Desktop Runtime - 8.* (x64)")]
    [InlineData("dotnet8-x86.json", "Microsoft Windows Desktop Runtime - 8.* (x86)")]
    [InlineData("7zip.json", "7-Zip [0-9]*")]
    public void ADefinitionReadsItsArpName(string file, string arpName) =>
        Assert.Equal(arpName, DefinitionFixture.Load(file).ArpName);

    [Fact]
    public void ADefinitionWithoutTheFieldsWritesNone()
    {
        var json = JsonSerializer.Serialize(new AppDefinition { Id = "x" });
        Assert.DoesNotContain("\"ArpName\"", json);
        Assert.DoesNotContain("\"ArpVersion\"", json);
        Assert.DoesNotContain("\"ArpArch\"", json);
    }

    [Fact]
    public void ADefinitionBecomesADiscoveryPackagerWithItsIdAndTrimmedFields()
    {
        var packager = DiscoveryPackagers.From(new AppDefinition
        {
            Id = "abc", Vendor = "Contoso", Application = "Widget (x64)", CMName = "", ArpName = " Widget* ", ArpVersion = " ", ArpArch = "X64"
        });
        Assert.Equal(new DiscoveryPackager("abc", "Contoso", "Widget (x64)", "Widget (x64)", "Widget*", null, "x64"), packager);
    }
}
