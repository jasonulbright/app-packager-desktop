using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>The Add site window: a site code, the SMS Provider and an optional label.</summary>
public partial class AddSiteViewModel : ObservableObject
{
    public const string Title = "Add site";

    [ObservableProperty] private string _siteCode = "";
    [ObservableProperty] private string _provider = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _validationMessage = "";

    /// <summary>True after Add accepted the values.</summary>
    public bool Accepted { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Accept()
    {
        var code = SiteCode.Trim();
        if (!SiteDataPaths.IsValidSiteCode(code))
        {
            ValidationMessage = SiteManager.InvalidCodeMessage(code);
            return;
        }
        ValidationMessage = "";
        SiteCode = code.ToUpperInvariant();
        Accepted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Accepted = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
