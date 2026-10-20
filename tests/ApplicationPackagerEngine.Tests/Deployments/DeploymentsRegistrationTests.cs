using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.Deployments;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DeploymentsRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-dhservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public DeploymentsRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ThePackagerServicesRegisterOneOfEachDeploymentServiceUnderTheDataRoot()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();

        var service = services.GetRequiredService<IDeploymentService>();
        Assert.IsType<DeploymentService>(service);
        Assert.Same(service, services.GetRequiredService<IDeploymentService>());
        Assert.Equal(Path.Combine(AppDataPaths.Root, "Deployments", "deployment-audit.jsonl"), services.GetRequiredService<IDeploymentAuditLog>().Path);
        Assert.Equal(4, services.GetRequiredService<IDeploymentTemplateStore>().All().Count);
        Assert.StartsWith(Path.Combine(AppDataPaths.Root, "Deployments", "Reports"), services.GetRequiredService<IDeploymentReportWriter>().WriteHtml([]));
    }

    [Fact]
    public async Task APathProviderRegisteredByThePageIsFollowedAtEveryCall()
    {
        var provider = new MutablePath();
        await using var services = new ServiceCollection().AddPackagerServices().AddSingleton<IDeploymentAuditPathProvider>(provider).BuildServiceProvider();
        var log = services.GetRequiredService<IDeploymentAuditLog>();

        provider.AuditLogPath = @"\\server\share\audit.jsonl";
        Assert.Equal(@"\\server\share\audit.jsonl", log.Path);
        provider.AuditLogPath = "";
        Assert.Equal(Path.Combine(AppDataPaths.Root, "Deployments", "deployment-audit.jsonl"), log.Path);
    }

    private sealed class MutablePath : IDeploymentAuditPathProvider
    {
        public string? AuditLogPath { get; set; }
    }
}
