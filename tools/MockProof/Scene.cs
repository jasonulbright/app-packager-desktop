using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApplicationPackagerUI;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace MockProof;

/// <summary>The running application with the data of the mock, and one method that brings it to each screen of the mock.</summary>
/// <summary>The screen of the mock that a method of <see cref="Scene"/> draws.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ScreenAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

internal sealed partial class Scene
{
    private readonly MainWindow _main;
    private readonly PackagesViewModel _packages;

    private Scene(MainWindow main, PackagesViewModel packages) => (_main, _packages) = (main, packages);

    public static async Task<Scene> StartAsync()
    {
        MainWindow? main = null;
        await Until(() => (main = System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault()) is { IsLoaded: true }, 60, "the main window");
        Size(main!, Program.Width, Program.Height);
        var packages = App.Services.GetRequiredService<PackagesViewModel>();
        await Until(() => packages.Packagers is { Count: > 0 } && !packages.LoadPackagersCommand.IsRunning, 90, "the application list");
        await Until(() => packages.MecmConnectionStatus is not ("Unknown" or "Testing..."), 90, "the connection test");
        await FitContentAsync(main!);
        var scene = new Scene(main!, packages);
        scene.LoadMockRows();
        packages.MecmConnectionStatus = "Connected";
        packages.StatusText = MockData.LastCheck;
        await scene.SettleAsync();
        return scene;
    }

    private void LoadMockRows()
    {
        _packages.Packagers = new ObservableCollection<PackagerInfo>(MockData.Rows());
        Invoke(_packages, "WatchRows");
        Invoke(_packages, "ApplyFilter", true, true);
    }

    private static void Invoke(object target, string name, params object[] arguments)
    {
        var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.Name == name).OrderBy(m => m.GetParameters().Length).Last();
        var parameters = method.GetParameters();
        var values = parameters.Select((p, i) => i < arguments.Length ? arguments[i] : p.DefaultValue).ToArray();
        method.Invoke(target, values);
    }

    /// <summary>Runs the method that carries the name of the screen. Each group of screens has its own file, Scene.&lt;group&gt;.cs.</summary>
    public async Task<BitmapSource> RenderAsync(string screen)
    {
        var method = typeof(Scene).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(m => m.GetCustomAttribute<ScreenAttribute>()?.Name == screen)
            ?? throw new ArgumentException("no scene for the screen " + screen);
        return await (Task<BitmapSource>)method.Invoke(this, null)!;
    }

    private async Task SettleAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            _main.UpdateLayout();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(250);
        }
    }

    private static async Task Until(Func<bool> done, int seconds, string what)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("waited for " + what);
            await Task.Delay(200);
        }
    }

    /// <summary>Gives the window the size of the mock, also on a display that is smaller than the mock.</summary>
    private static void Size(Window window, int width, int height, bool hook = true)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (hook) HwndSource.FromHwnd(handle)!.AddHook(AllowAnySize);
        window.WindowState = WindowState.Normal;
        window.MinWidth = width;
        window.MinHeight = height;
        window.MaxWidth = width;
        window.MaxHeight = height;
        window.Left = 0;
        window.Top = 0;
        window.Width = width;
        window.Height = height;
    }

    /// <summary>
    /// The size of the mock is the size of the content: a display without a desktop gives a window a frame, so the window grows until its
    /// content has that size.
    /// </summary>
    private static async Task FitContentAsync(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            var dx = Program.Width - (root.ActualWidth + root.Margin.Left + root.Margin.Right);
            var dy = Program.Height - (root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
            Size(window, (int)Math.Round(window.Width + dx), (int)Math.Round(window.Height + dy), false);
        }
    }

    private static IntPtr AllowAnySize(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int getMinMaxInfo = 0x0024;
        if (message != getMinMaxInfo) return IntPtr.Zero;
        // MINMAXINFO: five points of two integers; ptMaxTrackSize is the fifth.
        Marshal.WriteInt32(lParam, 32, 4000);
        Marshal.WriteInt32(lParam, 36, 4000);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>The content of the window on its own background, one pixel for each device independent unit.</summary>
    public static BitmapSource Capture(Window window, bool backdropFill = true)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        // The mock images have the colored smoothing of text that a screen shows. WPF draws it into a bitmap only with this hint.
        RenderOptions.SetClearTypeHint(root, ClearTypeHint.Enabled);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
        window.UpdateLayout();
        var width = (int)Math.Round(root.ActualWidth);
        var height = (int)Math.Round(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
        {
            var fill = window.Background ?? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush");
            context.DrawRectangle(fill, null, new Rect(0, 0, width, height));
        }
        if (backdropFill) bitmap.Render(backdrop);
        bitmap.Render(root);
        return bitmap;
    }
}
