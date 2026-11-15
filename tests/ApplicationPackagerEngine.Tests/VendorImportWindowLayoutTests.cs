using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.VendorImport;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The import window in both themes, empty and with a preview, at its default and its smallest size: no control is cut off or outside the window.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class VendorImportWindowLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-vendor-win-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private VendorImportViewModel Create()
    {
        var def = new AppDefinition
        {
            Id = "contoso-agent", Vendor = "Contoso", Application = "Contoso Agent", CMName = "Contoso Agent", Architecture = "x64",
            VendorUrl = "https://contoso.example/", InstallerType = "MSI", DownloadUrl = "https://contoso.example/agent-{version}.msi",
            InstallArgs = "/qn", Detection = new DetectionConfig { Type = "RegistryKey", RegistryKeyTemplate = @"SOFTWARE\Contoso" }
        };
        var body = $$"""{"FeedFormat":1,"Vendor":"Contoso Software","FeedId":"contoso-software","Definitions":[{{JsonSerializer.Serialize(def)}}]}""";
        var service = new VendorImportService(
            new ImportedDefinitionStore(_root), () => new HashSet<string>(), () => new HashSet<string>(),
            new FeedFetcher(() => new Handler(body), null, (_, _) => Task.CompletedTask));
        return new VendorImportViewModel(service, (_, _) => Task.FromResult(true));
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement root) =>
        new(element.TranslatePoint(new Point(0, 0), root), new Size(element.ActualWidth, element.ActualHeight));

    [Theory]
    [InlineData(ApplicationTheme.Light, 1000, 760, false)]
    [InlineData(ApplicationTheme.Dark, 1000, 760, false)]
    [InlineData(ApplicationTheme.Light, 1000, 760, true)]
    [InlineData(ApplicationTheme.Dark, 1000, 760, true)]
    [InlineData(ApplicationTheme.Light, 760, 560, true)]
    [InlineData(ApplicationTheme.Dark, 760, 560, true)]
    public void TheImportWindowKeepsItsControlsInsideTheWindow(ApplicationTheme theme, double width, double height, bool withPreview)
    {
        WithApplication(theme, () =>
        {
            var vm = Create();
            var window = new VendorImportWindow(vm) { Width = width, Height = height };
            Show(window);
            try
            {
                if (withPreview)
                {
                    vm.Address = "https://vendor.example/feed.json";
                    Run(vm.FetchCommand.ExecuteAsync(null));
                    Assert.True(vm.HasPreview);
                }
                Settle(window);
                var name = $"Import window {theme} {width}x{height} preview={withPreview}";
                var content = (FrameworkElement)window.Content;
                var inside = new Rect(0, 0, window.ActualWidth, window.ActualHeight);

                foreach (var id in new[] { "btnFetchFeed", "btnCancelFetch", "btnImportFeed", "btnCloseImport" })
                {
                    var button = ById<Button>(window, id);
                    var box = Bounds(button, content);
                    Assert.True(inside.Contains(box), $"{name}: {id} lies outside the window ({box}).");
                    Assert.True(box.Height >= 40 - 0.51, $"{name}: {id} is {box.Height} px high.");
                    Assert.True(button.ActualWidth + 0.51 >= button.DesiredSize.Width, $"{name}: {id} is cut off.");
                }
                var address = ById<TextBox>(window, "txtFeedAddress");
                Assert.True(address.ActualHeight >= 40 - 0.51 && address.ActualWidth >= 200, $"{name}: the address box is {address.ActualWidth} x {address.ActualHeight}.");
                Assert.True(Bounds(address, content).Right <= window.ActualWidth, $"{name}: the address box is wider than the window.");
                Assert.False(ById<Button>(window, "btnImportFeed").IsEnabled, $"{name}: Import is enabled before the confirmation.");
            }
            finally { window.Close(); }
        });
    }
}
