using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The text of every check agrees in number with its counts: "1 client", "2 clients", never "1 clients" or "client(s)".</summary>
public sealed partial class SiteHygieneEvidenceTextTests
{
    private static readonly string[] DeploymentColumns = ["DeploymentID", "SoftwareName", "ProgramName", "PackageID", "ModelName", "CollectionID", "CollectionName", "DeploymentIntent", "FeatureType", "NumberTargeted", "NumberSuccess", "NumberInProgress", "NumberErrors", "EnforcementDeadline", "CreationTime"];

    /// <summary>A count of one followed by a plural noun, with up to two words between them: "1 clients", "1 old revisions".</summary>
    [GeneratedRegex(@"(?<![\d.,])\b1 (?:[a-z]+ ){0,2}(?:clients|revisions|days|items|points|records|rules|changes|minutes|hours|levels|IDs|paths|updates|collections|objects|deployments|groups)\b")]
    private static partial Regex OnePlural();

    /// <summary>A noun with "(s)" in place of its number agreement.</summary>
    [GeneratedRegex(@"[A-Za-z]\(s\)(?=[\s.,:;'""])")]
    private static partial Regex OptionalPlural();

    /// <summary>A number interpolation followed by a plural word, which reads "1 clients" for a count of one.</summary>
    [GeneratedRegex(@"\{N\((?:[^(){}]|\([^(){}]*\))*\)\} (?:[a-z]+ ){0,2}(?!progress\b)[a-z]{2,}s\b")]
    private static partial Regex NumberThenPlural();

    /// <summary>A string literal of one source line, with its interpolation holes; code outside the quotes is not text.</summary>
    [GeneratedRegex(@"\$?""(?:[^""\\]|\\.)*""")]
    private static partial Regex StringLiteral();

    private static IEnumerable<string> Texts(HygieneFinding f) => [f.ObjectName, f.Evidence, f.Recommendation, f.Guidance];

    private static string ChecksFolder()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "src", "ApplicationPackagerEngine"))) folder = folder.Parent;
        return Path.Combine(folder!.FullName, "src", "ApplicationPackagerEngine", "Services", "SiteHygiene");
    }

    [Fact]
    public void NoCheckTemplateUsesAnOptionalPluralOrANumberFollowedByAFixedPlural()
    {
        var files = Directory.GetFiles(ChecksFolder(), "HygieneChecks*.cs");
        Assert.True(files.Length >= 5, $"Only {files.Length} check files were found.");
        var failures = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match literal in StringLiteral().Matches(lines[i]))
                    if (OptionalPlural().Match(literal.Value) is { Success: true } optional) failures.Add($"{Path.GetFileName(file)}:{i + 1}: {optional.Value}");
                if (NumberThenPlural().Match(lines[i]) is { Success: true } plural) failures.Add($"{Path.GetFileName(file)}:{i + 1}: {plural.Value}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ADeploymentThatTargetsOneClientSaysOneClient()
    {
        var read = SiteHygieneParity.EmptySite()
            .Put("Deployments", DeploymentColumns,
                ["{A}", "Failing App", "", "MCM00100", "", "MCM00200", "Pilot", "1", "1", "1", "0", "0", "1", "2026-10-01T00:00:00", "2026-09-01T00:00:00"],
                ["{D}", "Quiet App", "", "MCM00103", "", "MCM00400", "Pilot", "2", "1", "1", "0", "0", "0", "", "2026-01-01T00:00:00"]);
        var (findings, _) = SiteHygieneParity.Native(read, "Deployments");
        Assert.Contains("with 1 of 1 targeted client in error", Assert.Single(findings, f => f.CheckId == "DPL-02").Evidence);
        Assert.Contains("targets 1 client with zero installs", Assert.Single(findings, f => f.CheckId == "DPL-03").Evidence);
    }

    [Fact]
    public void NoFindingOfTheSiteCaptureSaysOneWithAPluralNounOrAnOptionalPlural()
    {
        var (findings, _) = SiteHygieneParity.Native(SiteHygieneService.ResolveAreas(new HashSet<string>()).ToArray());
        Assert.NotEmpty(findings);
        var failures = findings.SelectMany(f => Texts(f).Where(t => OnePlural().IsMatch(t) || t.Contains("(s)", StringComparison.Ordinal)).Select(t => $"{f.CheckId} {f.ObjectKey}: {t}")).ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
