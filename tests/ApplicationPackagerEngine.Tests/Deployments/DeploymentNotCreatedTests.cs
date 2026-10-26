using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The NotCreated marker of a create outcome through the real Deployments script and the stub site.</summary>
public sealed class DeploymentNotCreatedTests
{
    [Fact]
    public async Task ARefusedCreateIsMarkedNotCreated()
    {
        using var h = new DeploymentHarness();
        var outcome = await h.PrepareAndApplyAsync(DeploymentHarness.Create(DeploymentHarness.App, DeploymentHarness.Options(DeploymentObjectType.Application, "Required")));
        Assert.StartsWith(DeploymentService.SkippedPrefix, outcome.Message, StringComparison.Ordinal);
        Assert.True(outcome.NotCreated);
    }

    [Fact]
    public async Task ACreateWhoseContentDistributionFailedIsMarkedNotCreated()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(DeploymentHarness.Create(DeploymentHarness.Package, DeploymentHarness.Options(DeploymentObjectType.Package)));
        var failed = (await h.ApplyAsync(prepared with { DpGroups = ["Missing group"] })).Single();
        Assert.Null(failed.UndoChange);
        Assert.True(failed.NotCreated, failed.Message);
    }

    [Fact]
    public async Task ACreatedDeploymentIsNotMarked()
    {
        using var h = new DeploymentHarness();
        var outcome = await h.PrepareAndApplyAsync(DeploymentHarness.Create(DeploymentHarness.App, DeploymentHarness.Options(DeploymentObjectType.Application)));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False(outcome.NotCreated);
    }
}
