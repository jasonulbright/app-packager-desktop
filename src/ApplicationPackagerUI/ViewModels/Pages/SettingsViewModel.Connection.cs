using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// Settings, ConfigMgr: the connection signal beside the heading. It reads the same <see cref="SiteStateService"/> as the context strip,
/// so it describes the saved site and the last connection test, not the text that is being typed.
/// </summary>
public partial class SettingsViewModel
{
    private readonly SiteStateService? _siteState;

    /// <summary>"Connected to Site MCM", or what else is known about the connection; empty when nothing knows it.</summary>
    public string ConfigMgrSignalText => _siteState is null ? "" : SignalText(_siteState.Current);

    public SymbolRegular ConfigMgrSignalGlyph => _siteState is null ? SymbolRegular.Info24 : SignalGlyph(_siteState.Current.For(OneClickDestinations.ConfigMgr).State);

    /// <summary>The glyph of the mock's icon font for the state of the connection.</summary>
    public string ConfigMgrSignalIcon => _siteState?.Current.For(OneClickDestinations.ConfigMgr).State switch
    {
        SystemState.Connected => "checkmark_circle",
        SystemState.Failed => "error_circle",
        SystemState.NotConfigured => "warning",
        SystemState.Testing => "clock",
        _ => "info"
    };

    /// <summary>The color class of the signal: good, warn, bad or empty. Only a connection test that succeeded is good.</summary>
    public string ConfigMgrSignalKind => _siteState?.Current.For(OneClickDestinations.ConfigMgr).State switch
    {
        SystemState.Connected => "good",
        SystemState.Failed => "bad",
        SystemState.NotConfigured => "warn",
        _ => ""
    };

    /// <summary>Why ConfigMgr is not configured, or the signal itself.</summary>
    public string ConfigMgrSignalToolTip => _siteState?.Current.For(OneClickDestinations.ConfigMgr).Reason is { Length: > 0 } reason ? reason : ConfigMgrSignalText;

    /// <summary>Only a connection test that succeeded reads "Connected".</summary>
    public static string SignalText(ContextStripState state) => state.For(OneClickDestinations.ConfigMgr).State switch
    {
        SystemState.Connected => $"Connected to {state.SiteText}",
        SystemState.Failed => $"Connection to {state.SiteText} failed",
        SystemState.Testing => $"Testing the connection to {state.SiteText}",
        SystemState.Ready => $"{state.SiteText}: connection not tested",
        SystemState.NotConfigured => "ConfigMgr not configured",
        _ => "ConfigMgr off"
    };

    /// <summary>Ready has no success mark: nothing tested the connection.</summary>
    private static SymbolRegular SignalGlyph(SystemState state) => state == SystemState.Ready ? SymbolRegular.Info24 : ContextStripViewModel.GlyphOf(state);

    private void TrackSiteState()
    {
        if (_siteState is null) return;
        _siteState.Changed += () =>
        {
            void Raise()
            {
                OnPropertyChanged(nameof(ConfigMgrSignalText));
                OnPropertyChanged(nameof(ConfigMgrSignalGlyph));
                OnPropertyChanged(nameof(ConfigMgrSignalIcon));
                OnPropertyChanged(nameof(ConfigMgrSignalKind));
                OnPropertyChanged(nameof(ConfigMgrSignalToolTip));
            }
            // The service raises Changed on any thread.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Raise();
            else dispatcher.BeginInvoke(Raise);
        };
    }
}
