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
            if (definition.ArpVersion is { } version && (version.Trim().Length == 0 || version.Split('|').Any(v => v.Trim().Length == 0 || !ArpWildcard.TryCreate(v.Trim(), out _))))
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

    private static readonly string[] VersionSamples = ["7.0.0.0", "7.4.6.0", "7.5.4.0", "7.6.0.0", "7.10.0.0", "8.0.4710", "11.0.28.6", "17.0.16.8", "21.0.8.9", "25.0.1.8"];

    private static string Probe(string pattern)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c is '*' or '?') sb.Append('0');
            else if (c == '`' && i + 1 < pattern.Length) sb.Append(pattern[++i]);
            else if (c == '[') { sb.Append(pattern[i + 1]); i = pattern.IndexOf(']', i + 1); }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    internal static bool VersionsOverlap(string? first, string? second)
    {
        var a = ArpWildcard.Split(first);
        var b = ArpWildcard.Split(second);
        if (a.Count == 0 || b.Count == 0) return true;
        bool Accepts(IReadOnlyList<string> patterns, string version) =>
            patterns.Any(p => ArpWildcard.TryCreate(p, out var r) && r.IsMatch(version));
        return a.Concat(b).Select(Probe).Concat(VersionSamples).Any(v => Accepts(a, v) && Accepts(b, v));
    }

    /// <summary>
    /// Two definitions with the same name patterns overlap when their architectures can meet
    /// (an absent ArpArch meets every view) and a version one side accepts is accepted by the
    /// other (an absent ArpVersion accepts every version). Versions are probed with one
    /// concrete string per pattern and the shared sample list.
    /// </summary>
    internal static bool Overlap(AppDefinition first, AppDefinition second)
    {
        if (ArpWildcard.IsNone(first.ArpName) || ArpWildcard.IsNone(second.ArpName)) return false;
        static string Names(AppDefinition d) => string.Join("|", ArpWildcard.Split(d.ArpName).Select(p => p.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));
        if (Names(first) != Names(second)) return false;
        var archA = first.ArpArch?.Trim();
        var archB = second.ArpArch?.Trim();
        if (!string.IsNullOrEmpty(archA) && !string.IsNullOrEmpty(archB) && !string.Equals(archA, archB, StringComparison.OrdinalIgnoreCase)) return false;
        return VersionsOverlap(first.ArpVersion, second.ArpVersion);
    }

    [Fact]
    public void NoTwoDefinitionsWithTheSameNamePatternsAcceptTheSameInventoryRow()
    {
        var all = Definitions();
        var overlaps = new List<string>();
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
                if (Overlap(all[i].Definition, all[j].Definition)) overlaps.Add($"{all[i].File} and {all[j].File}");
        Assert.True(overlaps.Count == 0, string.Join(Environment.NewLine, overlaps));
    }

    [Fact]
    public void TheOverlapGuardCoversThePowerShellPair()
    {
        var stable = DefinitionFixture.Load("powershell7.json");
        var lts = DefinitionFixture.Load("powershell7lts.json");
        Assert.Equal(stable.ArpName, lts.ArpName);
        Assert.False(Overlap(stable, lts));
        Assert.True(Overlap(new AppDefinition { ArpName = stable.ArpName }, lts));
        Assert.True(VersionsOverlap("7.*", "7.4.*"));
        Assert.False(VersionsOverlap("17.*", "21.*"));
    }

    [Theory]
    [InlineData("7.6.0.0", true, false)]
    [InlineData("7.5.4.0", true, false)]
    [InlineData("7.10.1.0", true, false)]
    [InlineData("7.2.24.0", true, false)]
    [InlineData("7.4.6.0", false, true)]
    public void APowerShellVersionBelongsToExactlyOneDefinition(string version, bool stable, bool lts)
    {
        static bool Accepts(string file, string version)
        {
            var patterns = ArpWildcard.Split(DefinitionFixture.Load(file).ArpVersion);
            return patterns.Count == 0 || patterns.Any(p => ArpWildcard.TryCreate(p, out var r) && r.IsMatch(version));
        }
        Assert.Equal(stable, Accepts("powershell7.json", version));
        Assert.Equal(lts, Accepts("powershell7lts.json", version));
    }

    [Fact]
    public void TheLtsVersionPinFollowsTheSeriesItsScraperReads()
    {
        var lts = DefinitionFixture.Load("powershell7lts.json");
        var series = System.Text.RegularExpressions.Regex.Match(lts.VersionScraper.Pattern, @"v\((\d+)\\\.(\d+)\\\.").Groups;
        Assert.Equal($"{series[1].Value}.{series[2].Value}.*", lts.ArpVersion);
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
