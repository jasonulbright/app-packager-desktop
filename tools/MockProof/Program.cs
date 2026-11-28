using System.Collections.ObjectModel;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MockProof;

/// <summary>
/// MockProof &lt;output folder&gt; &lt;light|dark&gt; &lt;screen[,screen...]&gt;
/// Runs the application with an empty data folder and saves one 1930 x 1032 image for each screen.
/// </summary>
internal static class Program
{
    public const int Width = 1930, Height = 1032;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: MockProof <output> <light|dark> <screen[,screen...]>");
            return 2;
        }
        var output = Path.GetFullPath(args[0]);
        var theme = args[1].Equals("dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        var screens = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
        Directory.CreateDirectory(output);

        var data = Path.Combine(Path.GetTempPath(), "apd-mockproof-" + Guid.NewGuid().ToString("N"));
        var user = Path.Combine(data, "Users", WindowsIdentity.GetCurrent().User!.Value);
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Theme"] = theme,
            ["FirstRunCompleted"] = true,
            ["SiteCode"] = "MCM",
            ["ProviderMachine"] = "cm01.contoso.com",
            ["FileShareRoot"] = @"\\cm01.contoso.com\ContentShare$",
            ["UseConfigMgr"] = true,
            ["CheckForUpdatesAtStartup"] = false,
        }));
        Environment.SetEnvironmentVariable("APP_PACKAGER_DATA_ROOT", data);

        // A session without a desktop smooths text in gray. These settings apply until sign-out and are not stored in the profile.
        NativeText.UseSubpixelSmoothing();

        var failed = 0;
        var app = new ApplicationPackagerUI.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var scene = await Scene.StartAsync();
                foreach (var screen in screens)
                {
                    try
                    {
                        var bitmap = await scene.RenderAsync(screen);
                        Save(bitmap, Path.Combine(output, $"{screen}-{theme.ToLowerInvariant()}.png"));
                        Console.WriteLine($"saved {screen} {theme}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine($"FAILED {screen} {theme}: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAILED {theme}: {ex}");
            }
            finally
            {
                Console.Out.Flush();
                // The hosted services and background reads of the real application are not waited for: the images are saved.
                Environment.Exit(failed == 0 ? 0 : 1);
            }
        }, DispatcherPriority.ApplicationIdle);
        app.Run();
        return failed;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
