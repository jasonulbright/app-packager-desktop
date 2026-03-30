using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class MainWindow
{
    private readonly IServiceProvider _serviceProvider;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService)
    {
        _serviceProvider = serviceProvider;
        DataContext = viewModel;

        InitializeComponent();

        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        RootNavigation.SetServiceProvider(serviceProvider);

        // WPF-UI NavigationView swallows scroll events — tunnel to nearest ScrollViewer
        PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled) return;
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is System.Windows.Controls.ScrollViewer sv
                    && sv.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                {
                    sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                    e.Handled = true;
                    return;
                }
                try
                {
                    element = element is System.Windows.Media.Visual
                        ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                        : LogicalTreeHelper.GetParent(element);
                }
                catch { break; }
            }
        };
    }

    public Wpf.Ui.Controls.NavigationView GetNavigation() => RootNavigation;

    private void FluentWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        var settings = settingsService.Load();

        if (settings.WindowLeft >= 0 && settings.WindowTop >= 0)
        {
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
        RootNavigation.IsPaneOpen = settings.PaneOpen;

        Closing += (_, _) =>
        {
            var s = settingsService.Load();
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
            s.PaneOpen = RootNavigation.IsPaneOpen;
            settingsService.Save(s);
        };
    }
}
