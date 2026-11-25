using System.Collections.ObjectModel;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One system of the strip as the view draws it: the text, the glyph, the state key for the color and the reason as a tooltip.</summary>
public sealed record StripSystemItem(string Text, SymbolRegular Glyph, string StateKey, string ToolTip);

/// <summary>
/// The context strip at the top of every page (overhaul V1, phase 2): the site, the SMS Provider host and the state of ConfigMgr, Intune and WSUS.
/// Text only: no box here edits the site code or the host (SiteFooterTests). It follows <see cref="SiteStateService"/>.
/// </summary>
public partial class ContextStripViewModel : ObservableObject
{
    [ObservableProperty] private string _siteText = "";
    [ObservableProperty] private string _hostText = "";
    [ObservableProperty] private string _automationName = "";

    public ObservableCollection<StripSystemItem> Systems { get; } = [];

    public ContextStripViewModel() { }

    public ContextStripViewModel(SiteStateService state)
    {
        Apply(state.Current);
        // The service raises Changed on any thread; the strip is redrawn on the UI thread.
        state.Changed += () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Apply(state.Current);
            else dispatcher.BeginInvoke(() => Apply(state.Current));
        };
    }

    public static SymbolRegular GlyphOf(SystemState state) => state switch
    {
        SystemState.Off => SymbolRegular.Circle24,
        SystemState.NotConfigured => SymbolRegular.Warning24,
        SystemState.Failed => SymbolRegular.DismissCircle24,
        SystemState.Testing => SymbolRegular.ArrowSync24,
        _ => SymbolRegular.CheckmarkCircle24
    };

    public void Apply(ContextStripState state)
    {
        SiteText = state.SiteText;
        HostText = state.HostText;
        AutomationName = state.AutomationName;
        Systems.Clear();
        foreach (var system in state.Systems)
            Systems.Add(new StripSystemItem(system.Text, GlyphOf(system.State), system.State.ToString(),
                system.Reason.Length > 0 ? system.Reason : system.Text));
    }
}
