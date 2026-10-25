using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DetectionTesterRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-dtservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public DetectionTesterRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ThePackagerServicesRegisterOneOfEachDetectionTesterServiceUnderTheDataRoot()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();

        Assert.IsType<InstalledApplicationsService>(services.GetRequiredService<IInstalledApplicationsService>());
        Assert.IsType<DetectionTestService>(services.GetRequiredService<IDetectionTestService>());
        Assert.Same(services.GetRequiredService<IDetectionTestService>(), services.GetRequiredService<IDetectionTestService>());

        var history = services.GetRequiredService<IDetectionHistoryStore>();
        Assert.Same(history, services.GetRequiredService<IDetectionHistoryStore>());
        history.Add(new DetectionHistoryEntry(Guid.NewGuid(), DateTimeOffset.Now, "RegistryKey", "d", false, true, "s",
            new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "SOFTWARE" }));
        Assert.True(File.Exists(Path.Combine(AppDataPaths.Root, "DetectionTester", DetectionHistoryStore.FileName)));

        var report = services.GetRequiredService<IDetectionReportWriter>().WriteCsv(history.All());
        Assert.StartsWith(Path.Combine(AppDataPaths.Root, "DetectionTester", "Reports"), report);
    }
}
