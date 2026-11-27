using System.Diagnostics;
using System.Text.Json;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The release refuses a commit unless the CI workflow is green on it (release 2026.1108.10 was published while the
/// CI run of its commit was red). The decision is in tools/assert-ci-green.ps1; these tests run it on run lists.
/// </summary>
public sealed class ReleaseCiGateTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string Other = "fedcba9876543210fedcba9876543210fedcba98";

    private static string RepoFile(params string[] parts)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "AppPackagerDesktop.nsi"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine([folder.FullName, .. parts]);
    }

    private static object Run(long id, string sha, string status, string? conclusion, string created,
        string trigger = "push", string title = "ci") =>
        new { databaseId = id, headSha = sha, status, conclusion = conclusion ?? "", @event = trigger, createdAt = created, displayTitle = title };

    private static object FilteredRun(long id, string status, string? conclusion, string created) =>
        Run(id, Sha, status, conclusion, created, "workflow_dispatch", "[filtered] FullyQualifiedName~SomeTests");

    private static async Task<(int Exit, string Output)> GateAsync(string sha, params object[] runs)
    {
        var json = JsonSerializer.Serialize(runs);
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        start.Environment.Remove("PSModulePath");
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", RepoFile("tools", "assert-ci-green.ps1"), "-Sha", sha, "-RunsJson", json })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    [Fact]
    public async Task ARedRunOnTheCommitRefusesTheRelease()
    {
        var (exit, output) = await GateAsync(Sha, Run(1, Sha, "completed", "failure", "2026-11-08T10:00:00Z"));

        Assert.NotEqual(0, exit);
        Assert.Contains("ended failure", output);
    }

    [Fact]
    public async Task AGreenRunOnTheCommitPasses()
    {
        var (exit, output) = await GateAsync(Sha, Run(1, Sha, "completed", "success", "2026-11-08T10:00:00Z"));

        Assert.Equal(0, exit);
        Assert.Contains("succeeded", output);
    }

    [Fact]
    public async Task ARunStillRunningRefusesTheRelease()
    {
        var (exit, output) = await GateAsync(Sha,
            Run(1, Sha, "completed", "success", "2026-11-08T10:00:00Z"), Run(2, Sha, "in_progress", null, "2026-11-08T11:00:00Z"));

        Assert.NotEqual(0, exit);
        Assert.Contains("still in_progress", output);
    }

    [Fact]
    public async Task ACommitWithNoRunIsRefusedAndOtherCommitsRunsDoNotCount()
    {
        var (exit, output) = await GateAsync(Sha, Run(1, Other, "completed", "success", "2026-11-08T10:00:00Z"));

        Assert.NotEqual(0, exit);
        Assert.Contains("No CI run exists", output);
    }

    [Fact]
    public async Task ACancelledRunCountsForNothingAndTheNewestOtherRunDecides()
    {
        var (exit, _) = await GateAsync(Sha,
            Run(1, Sha, "completed", "failure", "2026-11-08T09:00:00Z"),
            Run(2, Sha, "completed", "success", "2026-11-08T10:00:00Z"),
            Run(3, Sha, "completed", "cancelled", "2026-11-08T11:00:00Z"));
        Assert.Equal(0, exit);

        var (exitOnlyCancelled, output) = await GateAsync(Sha, Run(1, Sha, "completed", "cancelled", "2026-11-08T10:00:00Z"));
        Assert.NotEqual(0, exitOnlyCancelled);
        Assert.Contains("cancelled", output);
    }

    [Fact]
    public async Task ARedRunAfterAGreenRunRefusesTheRelease()
    {
        var (exit, _) = await GateAsync(Sha,
            Run(1, Sha, "completed", "success", "2026-11-08T09:00:00Z"), Run(2, Sha, "completed", "failure", "2026-11-08T10:00:00Z"));

        Assert.NotEqual(0, exit);
    }

    [Theory]
    [InlineData("completed", "success")]
    [InlineData("completed", "failure")]
    [InlineData("queued", null)]
    [InlineData("in_progress", null)]
    public async Task AFilteredRunCountsForNothing(string status, string? conclusion)
    {
        // It cannot pass a commit.
        var (alone, output) = await GateAsync(Sha, FilteredRun(1, status, conclusion, "2026-11-08T10:00:00Z"));
        Assert.NotEqual(0, alone);
        Assert.Contains("No full CI run exists", output);

        // It cannot block a green full run, and it cannot hide a red one.
        var (green, _) = await GateAsync(Sha,
            Run(1, Sha, "completed", "success", "2026-11-08T09:00:00Z"), FilteredRun(2, status, conclusion, "2026-11-08T10:00:00Z"));
        Assert.Equal(0, green);

        var (red, redOutput) = await GateAsync(Sha,
            Run(1, Sha, "completed", "failure", "2026-11-08T09:00:00Z"), FilteredRun(2, status, conclusion, "2026-11-08T10:00:00Z"));
        Assert.NotEqual(0, red);
        Assert.Contains("ended failure", redOutput);
    }

    [Fact]
    public async Task AnUnfilteredManualRunCountsAndAQueuedOneRefuses()
    {
        var full = Run(1, Sha, "completed", "success", "2026-11-08T09:00:00Z", "workflow_dispatch");
        var (exit, _) = await GateAsync(Sha, full);
        Assert.Equal(0, exit);

        var (queued, output) = await GateAsync(Sha, full, Run(2, Sha, "queued", null, "2026-11-08T10:00:00Z", "workflow_dispatch"));
        Assert.NotEqual(0, queued);
        Assert.Contains("still queued", output);
    }

    [Fact]
    public async Task TheMarkerInTheCommitMessageOfAPushRunDoesNotHideTheRun()
    {
        var (exit, output) = await GateAsync(Sha,
            Run(1, Sha, "completed", "success", "2026-11-08T09:00:00Z"),
            Run(2, Sha, "completed", "failure", "2026-11-08T10:00:00Z", "push", "[filtered] a commit message"));

        Assert.NotEqual(0, exit);
        Assert.Contains("ended failure", output);
    }

    [Theory]
    [InlineData("displayTitle")]
    [InlineData("event")]
    public async Task ARunListWithoutAFieldThatTheCheckNeedsRefusesTheRelease(string field)
    {
        var run = JsonSerializer.SerializeToNode(Run(1, Sha, "completed", "success", "2026-11-08T10:00:00Z"))!.AsObject();
        Assert.True(run.Remove(field));

        var (exit, output) = await GateAsync(Sha, run);

        Assert.NotEqual(0, exit);
        Assert.Contains($"has no {field}", output);
    }

    [Fact]
    public void TheCiRunNameMarksAFilteredRunAndIsEmptyWithoutAFilter()
    {
        var yaml = File.ReadAllText(RepoFile(".github", "workflows", "ci.yml"));
        Assert.Contains("run-name: \"${{ inputs.filter && format('[filtered] {0}', inputs.filter) || '' }}\"", yaml);

        var gate = File.ReadAllText(RepoFile("tools", "assert-ci-green.ps1"));
        Assert.Contains("StartsWith('[filtered]'", gate);
    }

    [Fact]
    public void OnlyADryRunOfTheReleaseWorkflowRunsTheTests()
    {
        var yaml = File.ReadAllText(RepoFile(".github", "workflows", "release.yml")).ReplaceLineEndings("\n");

        Assert.Contains("- name: Set the display resolution for WPF tests\n        if: ${{ inputs.dry_run }}\n", yaml);
        Assert.Contains("- name: Engine tests\n        if: ${{ inputs.dry_run }}\n", yaml);
        Assert.Equal(2, yaml.Split("dotnet test ").Length);
    }

    [Fact]
    public void CheckoutKeepsNoCredentialAndOnlyTheTagPushPresentsTheToken()
    {
        var yaml = File.ReadAllText(RepoFile(".github", "workflows", "release.yml"));

        var checkout = yaml.IndexOf("actions/checkout@", StringComparison.Ordinal);
        var gate = yaml.IndexOf("- name: Require a green CI run on this commit", StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", yaml[checkout..gate]);

        var tag = yaml.IndexOf("- name: Tag the release commit", StringComparison.Ordinal);
        var publish = yaml.IndexOf("- name: Publish the GitHub release", StringComparison.Ordinal);
        var block = yaml[tag..publish];
        Assert.Contains("http.https://github.com/.extraheader=AUTHORIZATION: basic", block);
        Assert.Contains("GH_TOKEN: ${{ github.token }}", block);
    }
    [Fact]
    public void TheReleaseWorkflowRunsTheGateBeforeAnythingIsBuiltOrPublished()
    {
        var yaml = File.ReadAllText(RepoFile(".github", "workflows", "release.yml"));

        var gate = yaml.IndexOf("- name: Require a green CI run on this commit", StringComparison.Ordinal);
        var meta = yaml.IndexOf("- name: Resolve the version and its changelog section", StringComparison.Ordinal);
        var publish = yaml.IndexOf("- name: Publish the GitHub release", StringComparison.Ordinal);
        Assert.True(gate > 0 && gate < meta && meta < publish, "The CI gate must come before the version step and the publication.");

        var block = yaml[gate..meta];
        Assert.Contains("if: ${{ !inputs.dry_run }}", block);
        Assert.Contains("tools/assert-ci-green.ps1", block);
        Assert.Contains("--workflow ci.yml", block);
        Assert.Contains("--json databaseId,status,conclusion,event,createdAt,headSha,displayTitle", block);
        Assert.Contains("${{ github.sha }}", block);
        Assert.Contains("actions: read", yaml);
    }

    [Fact]
    public void ThePushTriggerSkipsGuideImageOnlyChangesAndNothingElse()
    {
        var yaml = File.ReadAllText(RepoFile(".github", "workflows", "ci.yml"));
        var push = yaml.IndexOf("  push:", StringComparison.Ordinal);
        var dispatch = yaml.IndexOf("  workflow_dispatch:", StringComparison.Ordinal);
        Assert.True(push > 0 && push < dispatch);
        var block = yaml[push..dispatch];

        Assert.Contains("paths-ignore:", block);
        Assert.Contains("- 'docs/images/**'", block);
        Assert.DoesNotContain(".md", block.Replace("README.md and docs/*.md", ""));
        // Only this pattern skips the job: code, tests, tools, the capture tool and workflows still run it.
        Assert.Equal(1, block.Split('\n').Count(l => l.TrimStart().StartsWith("- '", StringComparison.Ordinal)));
        Assert.DoesNotContain("paths:", block.Replace("paths-ignore:", ""));
    }
}
