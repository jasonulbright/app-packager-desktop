using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.Compliance;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ComplianceRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-compservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public ComplianceRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ThePackagerServicesRegisterOneOfEachComplianceServiceUnderTheDataRoot()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();

        var compliance = services.GetRequiredService<IComplianceService>();
        Assert.IsType<ComplianceService>(compliance);
        Assert.Same(compliance, services.GetRequiredService<IComplianceService>());
        var catalog = services.GetRequiredService<ICatalogService>();
        Assert.Same(catalog, services.GetRequiredService<ICatalogService>());
        Assert.Equal(16, catalog.All().Count);

        var report = services.GetRequiredService<IComplianceReportWriter>().WriteHtml(Array.Empty<ComplianceRow>());
        Assert.StartsWith(Path.Combine(AppDataPaths.Root, "Collections", "Reports"), report);
        Assert.StartsWith(Path.Combine(AppDataPaths.Root, "Collections", "Catalog"), CatalogBuilderRunner.DefaultFolder());
    }
}
