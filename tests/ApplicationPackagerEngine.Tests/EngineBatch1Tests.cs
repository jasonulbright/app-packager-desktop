using System.Diagnostics;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services.CmImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>State-machine batch 1: a process that hangs, and a loop that loses its partial results.</summary>
public sealed class EngineBatch1Tests
{
    [Fact]
    public void ACliThatWritesNothingAndNeverEndsIsGivenUpAtTheTimeLimit()
    {
        var timer = Stopwatch.StartNew();

        var token = GitHubAuth.ReadCliToken("powershell", "-NoProfile -Command Start-Sleep -Seconds 60", 1500);

        timer.Stop();
        Assert.Null(token);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), $"The read took {timer.Elapsed}.");
    }

    [Fact]
    public void ACliThatPrintsATokenAndEndsGivesIt()
    {
        var token = GitHubAuth.ReadCliToken("powershell", "-NoProfile -Command \"Write-Output ghp_example\"", 15000);

        Assert.Equal("ghp_example", token);
    }

    private static CmStagedApp Item(string name) => new(name, name, "", "", "", "");

    [Fact]
    public async Task AFailureOfTheThirdAppKeepsTheResultsOfTheFirstTwoAndNamesTheFailedOne()
    {
        var items = new[] { Item("A"), Item("B"), Item("C"), Item("D") };

        var ex = await Assert.ThrowsAsync<CmImportPartialException>(() => CmImportPublisher.PackageEachAsync(items, item =>
            item.Name == "C" ? throw new InvalidOperationException("Graph said no.")
                : Task.FromResult(new CmPackagedApp(item.Name, item.Name, "f.intunewin", "id-" + item.Name))));

        Assert.Equal(["A", "B"], ex.Completed.Select(p => p.Name));
        Assert.Equal("C", ex.FailedName);
        Assert.Contains("Graph said no.", ex.Message);
        Assert.Contains("A", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task ACancelIsNotWrapped()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => CmImportPublisher.PackageEachAsync([Item("A")],
            _ => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task EveryAppPackagedGivesItsResultInOrder()
    {
        var result = await CmImportPublisher.PackageEachAsync([Item("A"), Item("B")],
            item => Task.FromResult(new CmPackagedApp(item.Name, item.Name, "f", "")));

        Assert.Equal(["A", "B"], result.Select(p => p.Name));
    }
}
