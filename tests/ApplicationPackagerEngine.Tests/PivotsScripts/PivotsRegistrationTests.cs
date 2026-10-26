using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PivotsRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-psqservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public PivotsRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ThePivotsServicesAreRegisteredOverTheOneSessionWithTheDefaultFiles()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();
        var service = services.GetRequiredService<IPivotsScriptsService>();
        Assert.IsType<PivotsScriptsService>(service);
        Assert.Same(service, services.GetRequiredService<IPivotsScriptsService>());
        Assert.IsType<AdminServiceClient>(services.GetRequiredService<IAdminServiceClient>());
        var audit = services.GetRequiredService<IPivotsAuditLog>();
        Assert.Equal(Path.Combine(AppDataPaths.Root, "PivotsScripts", "pivots-scripts-audit.jsonl"), audit.Path);
        Assert.True(audit.Probe().Writable);
        Assert.Equal(Path.Combine(AppDataPaths.Root, "PivotsScripts", "Sessions"), services.GetRequiredService<IPivotsSessionStore>().Folder);
        Assert.Equal(Path.Combine(AppDataPaths.Root, "PivotsScripts", "Reports"), services.GetRequiredService<IPivotsReportWriter>().Folder);
        Assert.Equal(11, services.GetRequiredService<IPivotsLibrary>().Load(null).Count);
        Assert.Same(services.GetRequiredService<IConfigMgrSession>(), services.GetRequiredService<IConfigMgrSession>());
    }
}
