using ApplicationPackagerEngine.Tests.Compliance;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Owner term rule (lexicon LX-005, reader check 2026-11-23): "client" is the Configuration Manager agent on a device,
/// never the device itself. The settings text names devices.
/// </summary>
public sealed class ClientWordingTests
{
    private static readonly Regex DeviceAsClient = new(@"\bClients install\b|\btrusted on your clients\b", RegexOptions.Compiled);

    [Theory]
    [InlineData("src/ApplicationPackagerUI/Views/Pages/SettingsPage.xaml")]
    [InlineData("src/ApplicationPackagerUI/ViewModels/Pages/WsusPublishingEditor.cs")]
    [InlineData("docs/core/script-signing.md")]
    public void TheTextNamesDevicesAndNeverCallsADeviceAClient(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(CatalogServiceTests.RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.DoesNotMatch(DeviceAsClient, text);
    }
}
