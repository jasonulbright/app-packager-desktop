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

    private static object Run(long id, string sha, string status, string? conclusion, string created) =>
        new { databaseId = id, headSha = sha, status, conclusion = conclusion ?? "", @event = "push", createdAt = created };

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
        Assert.Contains("${{ github.sha }}", block);
        Assert.Contains("actions: read", yaml);
    }
}
