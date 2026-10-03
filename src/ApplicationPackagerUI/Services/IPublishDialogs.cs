using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>The windows and message boxes the publish actions open.</summary>
public interface IPublishDialogs
{
    void ShowMessage(string title, string message);

    /// <summary>Opens the WSUS Updates window for the server in <paramref name="settings"/>.</summary>
    void ShowWsusUpdates(WsusSettings settings);

    /// <summary>Opens the One Click plan window; it stays open while its run proceeds.</summary>
    void ShowOneClickPlan(OneClickPlanViewModel viewModel);
}
