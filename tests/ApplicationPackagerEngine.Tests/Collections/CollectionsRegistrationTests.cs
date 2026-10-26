using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.Collections;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CollectionsRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-collservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public CollectionsRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ThePackagerServicesRegisterOneOfEachCollectionsServiceUnderTheDataRoot()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();

        var collections = services.GetRequiredService<ICollectionService>();
        Assert.IsType<CollectionService>(collections);
        Assert.Same(collections, services.GetRequiredService<ICollectionService>());

        var templates = services.GetRequiredService<IQueryTemplateStore>();
        Assert.Same(templates, services.GetRequiredService<IQueryTemplateStore>());
        Directory.CreateDirectory(Path.Combine(AppDataPaths.Root, "Collections", "Templates"));
        File.WriteAllText(Path.Combine(AppDataPaths.Root, "Collections", "Templates", "mine.json"), """{ "Name": "Mine", "Query": "select * from SMS_R_System" }""");
        Assert.Contains(templates.All(), t => t is { Name: "Mine", IsShipped: false });

        var report = services.GetRequiredService<ICollectionReportWriter>().WriteHtml([]);
        Assert.StartsWith(Path.Combine(AppDataPaths.Root, "Collections", "Reports"), report);
    }
}
