using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The guard that keeps window tests off the dev PC: a test that opens a window runs only with APD_ALLOW_WINDOW_TESTS=1
/// and never on the computer AYW. None of these tests opens a window.
/// </summary>
public sealed class WindowTestGuardTests
{
    [Theory]
    [InlineData(null, "LAB-PC")]
    [InlineData("", "LAB-PC")]
    [InlineData("0", "LAB-PC")]
    [InlineData("true", "LAB-PC")]
    [InlineData("yes", "LAB-PC")]
    public void WithoutTheOptInTheWindowTestsAreSkippedWithTheVariableNamedInTheReason(string? optIn, string computer)
    {
        var reason = WindowTestGuard.SkipReason(optIn, computer);

        Assert.NotNull(reason);
        Assert.Contains("APD_ALLOW_WINDOW_TESTS=1", reason);
    }

    [Theory]
    [InlineData("1", "CM01")]
    [InlineData("1", "CLIENT01")]
    [InlineData(" 1 ", "fv-az123-456")]
    public void WithTheOptInOnAnotherComputerTheWindowTestsRun(string optIn, string computer) =>
        Assert.Null(WindowTestGuard.SkipReason(optIn, computer));

    [Theory]
    [InlineData("1", "AYW")]
    [InlineData("1", "ayw")]
    [InlineData("1", " AYW ")]
    [InlineData(null, "AYW")]
    [InlineData("0", "AYW")]
    public void TheDevPcRefusesWithAndWithoutTheOptIn(string? optIn, string computer)
    {
        var reason = WindowTestGuard.SkipReason(optIn, computer);

        Assert.NotNull(reason);
        Assert.Contains("AYW", reason);
        Assert.Contains("never run on the dev PC", reason);
    }

    [Fact]
    public void TheWindowTestAttributesTakeTheDecisionOfTheGuard()
    {
        var expected = WindowTestGuard.SkipReason();

        Assert.Equal(expected, new WindowFactAttribute().Skip);
        Assert.Equal(expected, new WindowTheoryAttribute().Skip);
    }

    private static string RepoFile(params string[] parts)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "AppPackagerDesktop.nsi"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine([folder.FullName, .. parts]);
    }

    private static IReadOnlyDictionary<string, string> TestSources()
    {
        var root = RepoFile("tests", "ApplicationPackagerEngine.Tests");
        var own = new[] { "WindowTestGuard.cs", "WindowTestSources.cs", "WindowTestGuardTests.cs" };
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(p => !own.Contains(Path.GetFileName(p)))
            .ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllText);
    }

    [Fact]
    public void EveryTestClassThatUsesWpfUsesTheWindowTestAttributes()
    {
        var sources = TestSources();
        var helpers = WindowTestSources.HelperNames(sources.Values);

        var untagged = sources
            .SelectMany(s => WindowTestSources.Untagged(s.Value, helpers).Select(c => $"{s.Key}: {c}"))
            .ToList();

        Assert.True(untagged.Count == 0,
            "These test classes use WPF and still carry [Fact] or [Theory]. Use [WindowFact] and [WindowTheory] so that they are skipped on the dev PC: "
            + string.Join("; ", untagged));
    }

    [Fact]
    public void NoTestClassWithoutWpfCarriesTheWindowTestAttributes()
    {
        var sources = TestSources();
        var helpers = WindowTestSources.HelperNames(sources.Values);

        var over = sources
            .SelectMany(s => WindowTestSources.OverTagged(s.Value, helpers).Select(c => $"{s.Key}: {c}"))
            .ToList();

        Assert.True(over.Count == 0,
            "These test classes do not use WPF and carry [WindowFact] or [WindowTheory], so they would be skipped without the opt-in. Use [Fact] and [Theory]: "
            + string.Join("; ", over));
    }

    [Fact]
    public void TheSourceScanFindsTheWindowTestClassesAndTheirHelpers()
    {
        var sources = TestSources();
        var helpers = WindowTestSources.HelperNames(sources.Values);
        var tagged = sources.Values.Sum(t => WindowTestSources.WindowTestClasses(t, helpers).Count);

        // A scan that finds nothing would pass the check above without checking anything.
        Assert.True(tagged >= 50, $"The scan found {tagged} window test classes.");
        Assert.Contains("StaTestHelper", helpers);
        Assert.Contains("RenderKit", helpers);
    }

    [Fact]
    public void ATestClassThatImportsAWpfTypeByAliasIsAWindowTestClass()
    {
        // CveWindowViewModelTests imports Point, Rect and Size from System.Windows by alias and opens no window itself.
        var sources = TestSources();
        var helpers = WindowTestSources.HelperNames(sources.Values);
        var file = sources.Single(s => s.Key.EndsWith("CveWindowAndGridTests.cs", StringComparison.Ordinal)).Value;

        Assert.Contains(WindowTestSources.WindowTestClasses(file, helpers), c => c.Name == "CveWindowViewModelTests");
    }

    [Fact]
    public void TheSourceScanSeesAPlainTestAttributeInAWindowClassAndAcceptsAWindowAttribute()
    {
        var helpers = new HashSet<string>();
        const string plain = "namespace T;\r\npublic class A\r\n{\r\n    [Fact]\r\n    public void X() { StaTestHelper.Run(() => { }); }\r\n}\r\n";
        const string tagged = "namespace T;\r\npublic class A\r\n{\r\n    [WindowFact]\r\n    public void X() { StaTestHelper.Run(() => { }); }\r\n}\r\n";
        const string pure = "namespace T;\r\npublic class B\r\n{\r\n    [Fact]\r\n    public void X() { }\r\n}\r\n";
        const string pureButTagged = "namespace T;\r\npublic class B\r\n{\r\n    [WindowFact]\r\n    public void X() { }\r\n}\r\n";

        Assert.Equal(["A"], WindowTestSources.Untagged(plain, helpers));
        Assert.Empty(WindowTestSources.Untagged(tagged, helpers));
        Assert.Empty(WindowTestSources.Untagged(pure, helpers));
        Assert.Contains("[WindowFact]", WindowTestSources.Normalize(plain, helpers));
        Assert.Equal(["B"], WindowTestSources.OverTagged(pureButTagged, helpers));
        Assert.Contains("[Fact]", WindowTestSources.Normalize(pureButTagged, helpers));
    }

    [Fact]
    public void WordsInCommentsAndStringsDoNotMakeATestClassAWindowTestClass()
    {
        var helpers = new HashSet<string>();
        // The patterns that a source scan looks for are text here, not code.
        const string scan = "namespace T;\r\npublic class S\r\n{\r\n    // new MainWindow() and StaTestHelper in a comment\r\n    private const string Pattern = \"new CatalogWindow(\";\r\n    private const string Raw = \"\"\"StaTestHelper.Run\"\"\";\r\n    private const string Verbatim = @\"Application.Current \"\"x\"\"\";\r\n    [Fact]\r\n    public void X() { }\r\n}\r\n";
        const string code = "namespace T;\r\npublic class S\r\n{\r\n    [Fact]\r\n    public void X() { var w = new CatalogWindow(); }\r\n}\r\n";
        const string alias = "using Point = System.Windows.Point;\r\nnamespace T;\r\npublic class S\r\n{\r\n    [Fact]\r\n    public void X() { }\r\n}\r\n";

        Assert.Empty(WindowTestSources.WindowTestClasses(scan, helpers));
        Assert.Single(WindowTestSources.WindowTestClasses(code, helpers));
        Assert.Single(WindowTestSources.WindowTestClasses(alias, helpers));
    }

    private static string Workflow(string name) => File.ReadAllText(RepoFile(".github", "workflows", name));

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    public void TheWorkflowOptsInToTheWindowTestsForEveryTestStep(string name)
    {
        var yaml = Workflow(name);

        // The opt-in is set in the top-level env block, and nowhere else with another value (a job or a step could
        // override it).
        var topLevel = Regex.Match(yaml, @"^env:\r?\n((?:[ \t]+.*\r?\n|[ \t]*\r?\n)+)", RegexOptions.Multiline);
        Assert.True(topLevel.Success, $"{name} has no top-level env block.");
        Assert.Matches(@"(?m)^\s+APD_ALLOW_WINDOW_TESTS:\s*""1""\s*$", topLevel.Groups[1].Value);
        foreach (Match setting in Regex.Matches(yaml, @"(?m)^\s*APD_ALLOW_WINDOW_TESTS:\s*(.*?)\s*$"))
            Assert.Equal("\"1\"", setting.Groups[1].Value);
        Assert.Contains("dotnet test tests/ApplicationPackagerEngine.Tests/ApplicationPackagerEngine.Tests.csproj", yaml);
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    public void TheWorkflowFailsWhenTooManyTestsWereSkipped(string name)
    {
        var yaml = Workflow(name);

        // A run in which the opt-in was lost skips every window test and still passes; the skipped count catches it.
        Assert.Contains("--logger \"trx;LogFileName=window-guard.trx\"", yaml);
        var step = yaml.IndexOf("- name: Window tests ran", StringComparison.Ordinal);
        Assert.True(step > yaml.IndexOf("dotnet test tests/", StringComparison.Ordinal), "The skipped-count step must follow the test step.");
        var text = yaml[step..];
        Assert.Contains("outcome -eq 'NotExecuted'", text);
        Assert.Matches(@"\$limit = \d+", text);
        Assert.Contains("if ($skipped -gt $limit) { throw", text);
    }

    [Fact]
    public void TheReadmeTellsHowToRunTheWindowTests() =>
        Assert.Contains("APD_ALLOW_WINDOW_TESTS", File.ReadAllText(RepoFile("README.md")));
}
