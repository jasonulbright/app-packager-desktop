using Xunit.Abstractions;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests.GuideImages;

/// <summary>
/// H-5: a guide image can be older than the last commit that changed the screen it shows. The map GuideImages.sources.json names the source files of each image;
/// tools\guide-capture\Get-StaleGuideImages.ps1 compares the last commit of the image with the last commit of its sources. The repository test reports the stale
/// images as test output and as a CI summary and does not fail, unless APD_GUIDE_STALE_MODE is "fail" (APD_GUIDE_STALE_MAX images may then be stale; default 0).
/// A shallow clone cannot answer the question: the test reports that and passes. The fixture tests run the script against small git repositories.
/// </summary>
public sealed class GuideImageStalenessTests(ITestOutputHelper output)
{
    public const string ModeVariable = "APD_GUIDE_STALE_MODE";
    public const string MaxVariable = "APD_GUIDE_STALE_MAX";

    private static string Root => CatalogServiceTests.RepositoryRoot();

    private sealed record Row(string Id, string Status, string? ImageDate, string? SourceDate, string? Reason, string[] ChangedFiles);

    private sealed record Report(string Summary, Row[] Images);

    private static (int Exit, string Out) Run(string fileName, string arguments, string workingDirectory, params (string Key, string Value)[] environment)
    {
        var start = new ProcessStartInfo(fileName, arguments) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, text + error.Result);
    }

    private static (Report Report, int Exit) RunScriptWithExit(string root, string? map = null, bool all = true, bool failOnStale = false)
    {
        var script = Path.Combine(Root, "tools", "guide-capture", "Get-StaleGuideImages.ps1");
        var arguments = $"-NoProfile -File \"{script}\" -Root \"{root}\" -Format Json" + (map is null ? "" : $" -MapPath \"{map}\"") + (all ? " -All" : "") + (failOnStale ? " -FailOnStale" : "");
        var (code, text) = Run("pwsh", arguments, root);
        return (JsonSerializer.Deserialize<Report>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException(text), code);
    }

    private static Report RunScript(string root, string? map = null, bool all = true, bool failOnStale = false) => RunScriptWithExit(root, map, all, failOnStale).Report;

    [Fact]
    public void TheStaleImagesOfTheRepositoryAreReported()
    {
        var report = RunScript(Root, all: false);
        var stale = report.Images.Where(i => i.Status == "Stale").ToList();
        var unmapped = report.Images.Where(i => i.Status == "Unmapped").Select(i => i.Id).ToList();
        var unknown = report.Images.Where(i => i.Status == "Unknown").ToList();

        output.WriteLine(report.Summary);
        foreach (var image in stale) output.WriteLine($"STALE {image.Id}: image {image.ImageDate}, source {image.SourceDate}, {image.Reason}");
        foreach (var image in unknown) output.WriteLine($"UNKNOWN {image.Id}: {image.Reason}");
        WriteCiSummary(report);

        Assert.True(unmapped.Count == 0, "GuideImages.sources.json has no rule for these images: " + string.Join(", ", unmapped));

        if (!string.Equals(Environment.GetEnvironmentVariable(ModeVariable), "fail", StringComparison.OrdinalIgnoreCase)) return;
        var allowed = int.TryParse(Environment.GetEnvironmentVariable(MaxVariable), out var max) ? max : 0;
        Assert.True(unknown.Count == 0, "The history is incomplete, so staleness cannot be checked (a shallow clone): " + unknown.FirstOrDefault()?.Reason);
        Assert.True(stale.Count <= allowed, $"{stale.Count} guide images are older than the last change to their screen (allowed {allowed}). Retake: " + string.Join(", ", stale.Select(i => i.Id)));
    }

    private static void WriteCiSummary(Report report)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(path)) return;
        var text = new StringBuilder().AppendLine("### Guide image staleness").AppendLine().AppendLine(report.Summary);
        var rows = report.Images.Where(i => i.Status != "Fresh").ToList();
        if (rows.Count > 0)
        {
            text.AppendLine().AppendLine("| Image | Status | Image date | Source date | Last source change |").AppendLine("|---|---|---|---|---|");
            foreach (var row in rows) text.AppendLine($"| {row.Id} | {row.Status} | {row.ImageDate} | {row.SourceDate} | {row.Reason?.Replace("|", "/")} |");
        }
        File.AppendAllText(path, text.AppendLine().ToString());
    }

    // ---- fixtures: small git repositories with controlled commit dates ----

    private sealed class Repo : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guide-stale-" + Guid.NewGuid().ToString("N"));
        private int clock;

        public Repo()
        {
            Directory.CreateDirectory(Path);
            Git("init", "-q", "-b", "main");
        }

        public string Git(params string[] arguments)
        {
            var (code, text) = Run("git", string.Join(" ", new[] { "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false" }.Concat(arguments).Select(a => a.Contains(' ') ? $"\"{a}\"" : a)), Path);
            Assert.True(code == 0, $"git {string.Join(' ', arguments)} failed: {text}");
            return text;
        }

        /// <summary>Writes the files and commits them one hour after the previous commit.</summary>
        public void Commit(string message, params (string File, string Content)[] files)
        {
            foreach (var (file, content) in files)
            {
                var full = System.IO.Path.Combine(Path, file);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }

            var date = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddHours(++clock).ToString("yyyy-MM-dd'T'HH:mm:ssK");
            Git("add", "-A");
            var (code, text) = Run("git", $"-c user.name=t -c user.email=t@example.com -c commit.gpgsign=false commit -q -m \"{message}\"", Path, ("GIT_AUTHOR_DATE", date), ("GIT_COMMITTER_DATE", date));
            Assert.True(code == 0, text);
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
    }

    private const string Map = """
        { "shared": ["src/Styles/"],
          "rules": [
            { "ids": ["tools-alpha-*"], "sources": ["src/Alpha*", "src/Shared/Common.cs"] },
            { "ids": ["tools-beta"], "sources": ["src/Beta.cs"] }
          ] }
        """;

    private static string MapPathOf(Repo repo) => System.IO.Path.Combine(repo.Path, "map.json");

    private static Repo NewRepo()
    {
        var repo = new Repo();
        repo.Commit("sources", ("src/Alpha.cs", "a"), ("src/AlphaView.xaml", "a"), ("src/Shared/Common.cs", "c"), ("src/Beta.cs", "b"), ("src/Styles/Theme.xaml", "t"), ("map.json", Map));
        return repo;
    }

    private static Row Of(Report report, string id) => report.Images.Single(i => i.Id == id);

    [Fact]
    public void AnImageThatIsNewerThanItsSourcesIsFresh()
    {
        using var repo = NewRepo();
        repo.Commit("images", ("docs/images/tools-alpha-one.png", "1"), ("docs/images/tools-beta.png", "1"));

        var report = RunScript(repo.Path, MapPathOf(repo));

        Assert.All(report.Images, i => Assert.Equal("Fresh", i.Status));
    }

    [Fact]
    public void ASourceChangeAfterTheImageMakesItStale()
    {
        using var repo = NewRepo();
        repo.Commit("images", ("docs/images/tools-alpha-one.png", "1"), ("docs/images/tools-beta.png", "1"));
        repo.Commit("change alpha view", ("src/AlphaView.xaml", "a2"));

        var report = RunScript(repo.Path, MapPathOf(repo));

        var alpha = Of(report, "tools-alpha-one");
        Assert.Equal("Stale", alpha.Status);
        Assert.Equal("change alpha view", alpha.Reason);
        Assert.Equal(["src/AlphaView.xaml"], alpha.ChangedFiles);
        Assert.Equal("Fresh", Of(report, "tools-beta").Status);
    }

    [Fact]
    public void AnImageAndItsSourcesInTheSameCommitAreFresh()
    {
        using var repo = NewRepo();
        repo.Commit("change and retake", ("src/Alpha.cs", "a2"), ("docs/images/tools-alpha-one.png", "1"), ("docs/images/tools-beta.png", "1"));

        Assert.All(RunScript(repo.Path, MapPathOf(repo)).Images, i => Assert.Equal("Fresh", i.Status));
    }

    [Fact]
    public void ARetakeAfterTheSourceChangeMakesTheImageFreshAgain()
    {
        using var repo = NewRepo();
        repo.Commit("images", ("docs/images/tools-alpha-one.png", "1"), ("docs/images/tools-beta.png", "1"));
        repo.Commit("change", ("src/Alpha.cs", "a2"));
        repo.Commit("retake", ("docs/images/tools-alpha-one.png", "2"));

        Assert.Equal("Fresh", Of(RunScript(repo.Path, MapPathOf(repo)), "tools-alpha-one").Status);
    }

    [Fact]
    public void ASharedSourceMakesEveryImageStaleAndAFileOfAnotherScreenDoesNot()
    {
        using var repo = NewRepo();
        repo.Commit("images", ("docs/images/tools-alpha-one.png", "1"), ("docs/images/tools-beta.png", "1"));
        repo.Commit("other screen", ("src/Unrelated.cs", "u"));
        Assert.All(RunScript(repo.Path, MapPathOf(repo)).Images, i => Assert.Equal("Fresh", i.Status));

        repo.Commit("theme", ("src/Styles/Theme.xaml", "t2"));

        Assert.All(RunScript(repo.Path, MapPathOf(repo)).Images, i => Assert.Equal("Stale", i.Status));
    }

    [Fact]
    public void AnImageWithNoRuleIsUnmappedAndAnUncommittedImageIsUnknown()
    {
        using var repo = NewRepo();
        repo.Commit("images", ("docs/images/no-rule.png", "1"));
        File.WriteAllText(System.IO.Path.Combine(repo.Path, "docs", "images", "tools-beta.png"), "x");

        var report = RunScript(repo.Path, MapPathOf(repo));

        Assert.Equal("Unmapped", Of(report, "no-rule").Status);
        Assert.Equal("Unknown", Of(report, "tools-beta").Status);
    }

    [Fact]
    public void AShallowCloneReportsUnknownAndNotStale()
    {
        using var repo = NewRepo();
        repo.Commit("images", ("docs/images/tools-alpha-one.png", "1"), ("docs/images/tools-beta.png", "1"));
        repo.Commit("change", ("src/Alpha.cs", "a2"));
        var clone = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guide-stale-clone-" + Guid.NewGuid().ToString("N"));
        try
        {
            repo.Git("clone", "-q", "--depth", "1", new Uri(repo.Path).AbsoluteUri, clone);

            var report = RunScript(clone, System.IO.Path.Combine(clone, "map.json"));

            Assert.All(report.Images, i => Assert.Equal("Unknown", i.Status));
            Assert.Contains("Shallow", Of(report, "tools-beta").Reason);
        }
        finally
        {
            if (Directory.Exists(clone))
            {
                foreach (var file in Directory.EnumerateFiles(clone, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(clone, true);
            }
        }
    }

    [Fact]
    public void TheFailSwitchOfTheScriptExitsWithOneOnlyForAStaleOrUnmappedImage()
    {
        using var repo = NewRepo();
        repo.Commit("images", ("docs/images/tools-alpha-one.png", "1"), ("docs/images/tools-beta.png", "1"));
        var fresh = RunScriptWithExit(repo.Path, MapPathOf(repo), failOnStale: true).Exit;
        repo.Commit("change", ("src/Beta.cs", "b2"));
        var stale = RunScriptWithExit(repo.Path, MapPathOf(repo), failOnStale: true).Exit;
        var reportOnly = RunScriptWithExit(repo.Path, MapPathOf(repo)).Exit;

        Assert.Equal(0, fresh);
        Assert.Equal(1, stale);
        Assert.Equal(0, reportOnly);
    }
}
