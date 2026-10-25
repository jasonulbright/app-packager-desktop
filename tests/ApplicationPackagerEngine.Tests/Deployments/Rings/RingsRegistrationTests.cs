using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RingsRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-ringservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public RingsRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task TheRingServiceIsRegisteredOverTheOneDeploymentServiceWithTheDefaultFolders()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();
        var rings = services.GetRequiredService<IRingService>();
        Assert.IsType<RingService>(rings);
        Assert.Same(rings, services.GetRequiredService<IRingService>());
        Assert.Same(services.GetRequiredService<IDeploymentService>(), services.GetRequiredService<IDeploymentSiteReader>());
        var probe = rings.ProbeFolders();
        Assert.Equal(Path.Combine(AppDataPaths.Root, "Deployments", "Rings"), probe.PlansFolder);
        Assert.Equal(Path.Combine(AppDataPaths.Root, "Deployments", "Rings", "runs"), probe.RunsFolder);
        Assert.True(probe.PlansWritable && probe.RunsWritable && probe.LockCreated, probe.Message);
        Assert.False(probe.IsUnc);
        Assert.Equal(TimeSpan.FromHours(1), probe.StaleLockAge);
        Assert.Equal(["Server-Rings", "Workstation-Rings"], rings.ListPlans().Select(p => p.Name));
    }

    [Fact]
    public async Task FolderPathsSetByThePageAreFollowedAtEveryCall()
    {
        var paths = new MutablePaths();
        await using var services = new ServiceCollection().AddPackagerServices().AddSingleton<IRingFolderPathProvider>(paths).BuildServiceProvider();
        var rings = services.GetRequiredService<IRingService>();
        paths.PlansFolder = Path.Combine(_root, "team", "plans");
        paths.RunsFolder = Path.Combine(_root, "team", "runs");
        var probe = rings.ProbeFolders();
        Assert.Equal((paths.PlansFolder, paths.RunsFolder), (probe.PlansFolder, probe.RunsFolder));
        rings.ListPlans();
        Assert.True(File.Exists(Path.Combine(paths.PlansFolder, "Server-Rings.json")));
        paths.RunsFolder = "relative\\runs";
        var bad = rings.ProbeFolders();
        Assert.False(bad.RunsWritable);
        Assert.Contains("full path", bad.Message);
    }

    private sealed class MutablePaths : IRingFolderPathProvider
    {
        public string? PlansFolder { get; set; }
        public string? RunsFolder { get; set; }
    }
}
