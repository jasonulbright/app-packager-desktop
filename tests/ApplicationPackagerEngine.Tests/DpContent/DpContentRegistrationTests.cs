using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.DpContent;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DpContentRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-dpcservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public DpContentRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task TheDpContentServicesAreRegisteredOverTheOneSessionWithTheDefaultFiles()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();
        var service = services.GetRequiredService<IDpContentService>();
        Assert.IsType<DpContentService>(service);
        Assert.Same(service, services.GetRequiredService<IDpContentService>());
        var audit = services.GetRequiredService<IDpContentAuditLog>();
        Assert.Equal(Path.Combine(AppDataPaths.Root, "DpContent", "dp-content-audit.jsonl"), audit.Path);
        Assert.True(audit.Probe().Writable);
        Assert.Equal(Path.Combine(AppDataPaths.Root, "DpContent", "Reports"), services.GetRequiredService<IDpContentReportWriter>().Folder);
        Assert.Same(services.GetRequiredService<IConfigMgrSession>(), services.GetRequiredService<IConfigMgrSession>());
    }
}
