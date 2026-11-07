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
    public void TheSourceScanSeesAPlainTestAttributeInAWindowClassAndAcceptsAWindowAttribute()
    {
        var helpers = new HashSet<string>();
        const string plain = "namespace T;\r\npublic class A\r\n{\r\n    [Fact]\r\n    public void X() { StaTestHelper.Run(() => { }); }\r\n}\r\n";
        const string tagged = "namespace T;\r\npublic class A\r\n{\r\n    [WindowFact]\r\n    public void X() { StaTestHelper.Run(() => { }); }\r\n}\r\n";
        const string pure = "namespace T;\r\npublic class B\r\n{\r\n    [Fact]\r\n    public void X() { }\r\n}\r\n";

        Assert.Equal(["A"], WindowTestSources.Untagged(plain, helpers));
        Assert.Empty(WindowTestSources.Untagged(tagged, helpers));
        Assert.Empty(WindowTestSources.Untagged(pure, helpers));
        Assert.Contains("[WindowFact]", WindowTestSources.Tag(plain, helpers));
    }

    [Fact]
    public void TheCiAndReleaseWorkflowsOptInToTheWindowTests()
    {
        foreach (var workflow in new[] { "ci.yml", "release.yml" })
        {
            var yaml = File.ReadAllText(RepoFile(".github", "workflows", workflow));
            Assert.True(yaml.Contains("APD_ALLOW_WINDOW_TESTS: \"1\"", StringComparison.Ordinal), $"{workflow} does not set APD_ALLOW_WINDOW_TESTS.");
        }
    }

    [Fact]
    public void TheReadmeTellsHowToRunTheWindowTests() =>
        Assert.Contains("APD_ALLOW_WINDOW_TESTS", File.ReadAllText(RepoFile("README.md")));
}
