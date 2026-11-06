using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Discovery;

public class DiscoveryMatcherTests
{
    /// <summary>
    /// A title row. The publisher, the client count and the client ordinals are accepted so that the
    /// fixtures read as before, and dropped: discovery reads application names and versions only.
    /// </summary>
    private static InventoryRow Row(string name, string publisher, string version, string arch, int count = 1, int[]? clients = null) =>
        new(name, version, arch);

    private static InventorySnapshot Snapshot(IEnumerable<InventoryRow> rows, params string[] applications) =>
        new(rows.ToList(), applications);

    private static DiscoveryPackager Packager(string id, string cmName, string arpName, string vendor = "Vendor", string? version = null, string? arch = null) =>
        new(id, vendor, cmName + " app", cmName, arpName, version, arch);

    private static IReadOnlyList<DiscoveryCandidate> Find(InventorySnapshot snapshot, IEnumerable<DiscoveryPackager> packagers,
        IEnumerable<string> hidden, IEnumerable<string>? dismissed = null) =>
        DiscoveryMatcher.FindCandidates(snapshot, DiscoveryMatcher.Prepare(packagers.ToList(), hidden.ToList(), (dismissed ?? []).ToList()));

    private static InventoryRow[] Rows() =>
    [
        Row("7-Zip 26.03", "Igor Pavlov", "26.03", "x86", 1),
        Row("7-Zip 26.03 (x64)", "Igor Pavlov", "26.03", "x64", 3),
        Row("Notepad++ (64-bit x64)", "Notepad++ Team", "8.8", "x64", 2),
        Row("Mozilla Firefox (x64 en-US)", "Mozilla", "144.0", "x64", clients: [1, 2, 3, 4]),
        Row("Mozilla Maintenance Service", "Mozilla", "144.0", "x64", clients: [1, 2, 3, 4]),
        Row("Audacity 3.7.9", "Audacity Team", "3.7.9", "x64", 1),
        Row("Java 8 Update 471", "Oracle", "8.0.4710", "x86", 1),
        Row("Java 8 Update 471 (64-bit)", "Oracle", "8.0.4710", "x64", 2),
    ];

    [Fact]
    public void ThirtyTwoAndSixtyFourBitRowsMergeIntoOneCandidate()
    {
        var c = Find(Snapshot(Rows()), [Packager("7z", "7-Zip", "7-Zip *", "Igor Pavlov")], ["7z"]);
        var only = Assert.Single(c);
        Assert.Equal(new DiscoveryCandidate("7z", "7-Zip", "Igor Pavlov", "7-Zip app"), only);
    }

    [Fact]
    public void AWildcardMatchesIgnoringCase() =>
        Assert.Equal("npp", Assert.Single(Find(Snapshot(Rows()), [Packager("npp", "Notepad++", "NOTEPAD++*")], ["npp"])).Id);

    [Fact]
    public void AnyPatternOfAMultiPatternArpNameMatches() =>
        Assert.Equal("j8", Assert.Single(Find(Snapshot(Rows()), [Packager("j8", "Java 8", "Java 8 Update * (64-bit) | Java 8 Update ?*")], ["j8"])).Id);

    [Fact]
    public void ARowGivesOneCandidateWhenTwoPatternsMatchIt() =>
        Assert.Equal("ff", Assert.Single(Find(Snapshot(Rows()), [Packager("ff", "Firefox", "Mozilla Firefox* | Mozilla Firefox (x64*")], ["ff"])).Id);

    [Fact]
    public void TwoMatchingEntriesGiveOneCandidate()
    {
        var snapshot = Snapshot([
            Row("Java 8 Update 471", "Oracle", "8.0.4710", "x86", clients: [1, 2]),
            Row("Java 8 Update 471 (64-bit)", "Oracle", "8.0.4710", "x64", clients: [2, 3]),
            Row("Java 8 Update 461 (64-bit)", "Oracle", "8.0.4610", "x64", clients: [3]),
        ]);
        Assert.Equal("j8", Assert.Single(Find(snapshot, [Packager("j8", "Java 8", "Java 8 Update *")], ["j8"])).Id);
    }

    [Fact]
    public void ANoneOrEmptyArpNameIsSkipped()
    {
        var packagers = new[] { Packager("a", "A", "none"), Packager("b", "B", " NONE "), Packager("c", "C", ""), Packager("d", "D", " | none") };
        Assert.Empty(Find(Snapshot(Rows()), packagers, ["a", "b", "c", "d"]));
    }

    [Fact]
    public void ADismissedPackagerIsSkippedIgnoringCase() =>
        Assert.Empty(Find(Snapshot(Rows()), [Packager("Seven", "7-Zip", "7-Zip*")], ["seven"], ["SEVEN"]));

    [Fact]
    public void APackagerThatIsNotHiddenIsSkipped()
    {
        Assert.Empty(Find(Snapshot(Rows()), [Packager("7z", "7-Zip", "7-Zip*")], ["other"]));
        Assert.Empty(Find(Snapshot(Rows()), [Packager("7z", "7-Zip", "7-Zip*")], []));
    }

    [Fact]
    public void APackagerWhoseCMNameIsAlreadyAnApplicationIsSkippedIgnoringCase() =>
        Assert.Empty(Find(Snapshot(Rows(), "audacity 3.7.9", "CMTrace Open"), [Packager("au", "Audacity 3.7.9", "Audacity*")], ["au"]));

    [Theory]
    [InlineData("M365 Apps for Enterprise", "Microsoft 365 Apps for enterprise - *", "M365 Apps for Enterprise (x64) (MEC)")]
    [InlineData("M365 Apps for Enterprise", "Microsoft 365 Apps for enterprise - *", "M365 Apps for Enterprise - 16.0.19127.20264 (x64) (MEC)")]
    [InlineData("Audacity", "Audacity [0-9]*", "Audacity 3.7.9")]
    [InlineData("7-Zip", "7-Zip [0-9]*", "7-Zip")]
    [InlineData("7-Zip", "7-Zip [0-9]*", "7-zip 26.03 (x64)")]
    [InlineData("Notepad++", "Notepad++ (64-bit x64)", "Notepad++ (64-bit x64)")]
    [InlineData("Mozilla Firefox", "Mozilla Firefox (x64 *)", "Mozilla Firefox (en-US)")]
    public void APackagerIsSkippedWhenConfigMgrHasAPackagedTitle(string cmName, string arpName, string title)
    {
        var snapshot = Snapshot([
            Row("Microsoft 365 Apps for enterprise - en-us", "Microsoft Corporation", "16.0.19127.20264", "x64", 2),
            Row("Audacity 3.7.9", "Audacity Team", "3.7.9", "x64", 1),
            Row("7-Zip 26.03 (x64)", "Igor Pavlov", "26.03", "x64", 3),
            Row("Notepad++ (64-bit x64)", "Notepad++ Team", "8.8", "x64", 2),
            Row("Mozilla Firefox (x64 en-US)", "Mozilla", "144.0", "x64", 4),
        ], "CMTrace Open", title);
        Assert.Empty(Find(snapshot, [Packager("p", cmName, arpName)], ["p"]));
    }

    [Theory]
    [InlineData("Git", "Git", "Git", "Git Extensions 4.3")]
    [InlineData("Git", "Git", "Git", "GitHub Desktop")]
    [InlineData("Mozilla Firefox", "Mozilla Firefox (x64 *)", "Mozilla Firefox (x64 en-US)", "Mozilla Firefox ESR (x64 en-US)")]
    [InlineData("M365 Apps for Enterprise", "Microsoft 365 Apps for enterprise - *", "Microsoft 365 Apps for enterprise - en-us", "M365 Apps for Enterprise Project (x64)")]
    [InlineData("Microsoft OpenJDK 17 (EXE)", "Microsoft Build of OpenJDK with Hotspot 17*", "Microsoft Build of OpenJDK with Hotspot 17.0.16+8 (x64)", "Microsoft OpenJDK 17 (MSI) (EXE)")]
    [InlineData("Amazon Corretto 11 (x64)", "Amazon Corretto 11*", "Amazon Corretto 11.0.28 (x64)", "Amazon Corretto 11 (x86) (x64)")]
    public void APackagerIsKeptWhenConfigMgrHasOnlyAnotherProductWithTheSameWords(string cmName, string arpName, string row, string title)
    {
        var c = Find(Snapshot([Row(row, "P", "1.0", "x64", 2)], title), [Packager("p", cmName, arpName)], ["p"]);
        Assert.Equal("p", Assert.Single(c).Id);
    }

    [Fact]
    public void ZeroInventoryRowsGiveNoCandidate() =>
        Assert.Empty(Find(new InventorySnapshot([], []), [Packager("7z", "7-Zip", "7-Zip*")], ["7z"]));

    [Fact]
    public void AHiddenPackagerWithNoMatchingRowIsSkipped() =>
        Assert.Empty(Find(Snapshot(Rows()), [Packager("vlc", "VLC", "VLC media player*")], ["vlc"]));

    [Theory]
    [InlineData("Odd [Name", null)]
    [InlineData("[", null)]
    [InlineData("Git | Odd[", null)]
    [InlineData("Odd*", "17.[0")]
    [InlineData("Odd [z-a]*", null)]
    [InlineData("Odd*", "7.[9-0].*")]
    public void AMalformedPatternThrowsAPlainMessageThatNamesTheDefinition(string arpName, string? arpVersion)
    {
        var ex = Assert.Throws<DiscoveryException>(() =>
            DiscoveryMatcher.Prepare([new DiscoveryPackager("odd", "V", "Odd Tool", "Odd", arpName, arpVersion, null)], ["odd"], []));
        var tag = arpVersion is null ? "ArpName" : "ArpVersion";
        Assert.StartsWith($"The definition for Odd Tool has an {tag} pattern that is not a valid wildcard", ex.Message);
    }

    [Fact]
    public void AReversedRangeIsNotAValidWildcard() =>
        Assert.False(ArpWildcard.TryCreate("[z-a]", out _));

    [Fact]
    public void AMatchTimeoutThrowsAPlainMessageThatNamesTheDefinition()
    {
        var slow = new System.Text.RegularExpressions.Regex("^(a+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var packager = new MatchablePackager(new DiscoveryPackager("slow", "V", "Slow Tool", "Slow", "a*", null, null), [slow], [], null);
        var snapshot = Snapshot([Row(new string('a', 40) + "!", "P", "1", "x64")]);
        var ex = Assert.Throws<DiscoveryException>(() => DiscoveryMatcher.FindCandidates(snapshot, [packager]));
        Assert.Equal("Matching the definition for Slow Tool against inventory took too long. Check its ArpName and ArpVersion patterns.", ex.Message);
    }

    [Fact]
    public void MatchingUsesInvariantCultureRules()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Single(Find(Snapshot([Row("INKSCAPE 1.4", "Inkscape", "1.4", "x64")]), [Packager("ink", "Inkscape", "inkscape*")], ["ink"]));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void ArpVersionAndArpArchLimitTheRows()
    {
        var snapshot = Snapshot([
            Row("Amazon Corretto (x64)", "Amazon", "17.0.16.8", "x64", clients: [1]),
            Row("Amazon Corretto (x64)", "Amazon", "21.0.8.9", "x64", clients: [2, 3]),
            Row("Amazon Corretto (x86)", "Amazon", "17.0.16.8", "x86", clients: [4]),
        ]);
        var packagers = new[]
        {
            Packager("c17", "Corretto 17", "Amazon Corretto (x??)", version: "17.*", arch: "x64"),
            Packager("c21", "Corretto 21", "Amazon Corretto (x64)", version: "21.*"),
            Packager("c25", "Corretto 25", "Amazon Corretto (x64)", version: "25.*"),
            Packager("c17x86", "Corretto 17 x86", "Amazon Corretto*", arch: "X86"),
            Packager("carm", "Corretto arm64", "Amazon Corretto*", arch: "arm64"),
        };
        var c = Find(snapshot, packagers, packagers.Select(p => p.Id));
        Assert.Equal("c17,c17x86,c21", string.Join(",", c.Select(x => x.Id)));
    }

    [Fact]
    public void CandidatesAreSortedByCMName()
    {
        var packagers = new[] { Packager("npp", "Notepad++", "Notepad++*"), Packager("7z", "7-Zip", "7-Zip*"), Packager("ff", "Firefox", "Mozilla Firefox*") };
        Assert.Equal(["7-Zip", "Firefox", "Notepad++"], Find(Snapshot(Rows()), packagers, ["npp", "7z", "ff"]).Select(c => c.CMName));
    }

    [Fact]
    public void DiscoveryCarriesNoClientKeyNoClientCountAndNoClientTotal()
    {
        Assert.Equal(["Id", "CMName", "Vendor", "Application"], typeof(DiscoveryCandidate).GetProperties().Select(p => p.Name));
        Assert.Equal(["Candidates", "StatusText"], typeof(DiscoveryResult).GetProperties().Select(p => p.Name));
        Assert.Equal(["DisplayName", "Version", "Architecture"], typeof(InventoryRow).GetProperties().Select(p => p.Name));
        Assert.Equal(["Rows", "Applications"], typeof(InventorySnapshot).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void TheStatusSaysNoInventoryDataForZeroRows()
    {
        var text = DiscoveryMatcher.StatusText(new InventorySnapshot([], []), 0, new DateTime(2026, 10, 17, 12, 0, 0));
        Assert.StartsWith("No inventory data", text);
        Assert.DoesNotContain("Discovered", text);
    }

    [Fact]
    public void TheStatusStatesOnlyTheCandidateCount()
    {
        var now = new DateTime(2026, 10, 17, 12, 0, 0);
        var snapshot = new InventorySnapshot(new List<InventoryRow> { Row("A", "P", "1", "x86") }, []);
        Assert.Equal("Discovered 3 apps.", DiscoveryMatcher.StatusText(snapshot, 3, now));
        Assert.Equal("Discovered 1 app.", DiscoveryMatcher.StatusText(snapshot, 1, now));
        Assert.Equal("Discovered 0 apps.", DiscoveryMatcher.StatusText(snapshot, 0, now));
    }

    [Theory]
    [InlineData("Widget*", "widget pro", true)]
    [InlineData("Widget [0-9]*", "Widget 2", true)]
    [InlineData("Widget [0-9]*", "Widget X", false)]
    [InlineData("Widget ?", "Widget 7", true)]
    [InlineData("Widget ?", "Widget 77", false)]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++ (64-bit x64)", true)]
    [InlineData("A`*B", "A*B", true)]
    [InlineData("A`*B", "AxB", false)]
    [InlineData("Microsoft Visual C++ 2015-20?? Redistributable (x64) - *", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", true)]
    [InlineData("Widget", "Widget\n", false)]
    [InlineData("7.[0-35-9].*", "7.5.4.0", true)]
    [InlineData("7.[0-35-9].*", "7.4.6.0", false)]
    [InlineData("7.[0-9][0-9]*", "7.10.0.0", true)]
    [InlineData("7.[0-9][0-9]*", "7.4.6.0", false)]
    public void AnArpWildcardFollowsPowerShellLikeSyntax(string pattern, string text, bool expected)
    {
        Assert.True(ArpWildcard.TryCreate(pattern, out var regex));
        Assert.Equal(expected, regex.IsMatch(text));
    }
}
