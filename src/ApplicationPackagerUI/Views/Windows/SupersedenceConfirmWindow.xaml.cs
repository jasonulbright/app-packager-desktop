using System.Windows;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>
/// The confirmation before a supersedence is applied: Yes applies it, Skip packages without it, No stops the whole run. "Remember"
/// keeps the answer for the other applications of the same run. Skip is the default, so Enter and Escape never stop a run.
/// </summary>
public partial class SupersedenceConfirmWindow
{
    public SupersedenceConfirmWindow(SupersedencePrompt prompt)
    {
        InitializeComponent();
        txtQuestion.Text = "Supersede the older version of this application?";
        txtOld.Text = $"Older version found: {prompt.OldTitle} ({prompt.OldVersion})";
        txtNew.Text = $"Packaging now: {prompt.NewTitle} ({prompt.NewVersion})";
        txtEffect.Text = prompt.Uninstall
            ? "Yes: the new application supersedes the older one and uninstalls it. The older application itself is not changed."
            : "Yes: the new application supersedes the older one. The older application itself is not changed.";
    }

    /// <summary>The choice; Skip until a button is pressed.</summary>
    public SupersedenceDecision Decision { get; private set; } = new(SupersedenceAnswer.Skip, false);

    private void Choose(SupersedenceAnswer answer)
    {
        Decision = new SupersedenceDecision(answer, chkRemember.IsChecked == true);
        DialogResult = true;
    }

    private void OnYes(object sender, RoutedEventArgs e) => Choose(SupersedenceAnswer.Yes);

    private void OnSkip(object sender, RoutedEventArgs e) => Choose(SupersedenceAnswer.Skip);

    private void OnNo(object sender, RoutedEventArgs e) => Choose(SupersedenceAnswer.No);
}

/// <summary>The WPF side of the confirmation; it opens on the interface thread whichever thread the run is on.</summary>
public static class SupersedenceConfirmDialog
{
    public static Task<SupersedenceDecision> AskAsync(SupersedencePrompt prompt)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.FromResult(new SupersedenceDecision(SupersedenceAnswer.Skip, false));
        return dispatcher.InvokeAsync(() =>
        {
            var window = new SupersedenceConfirmWindow(prompt);
            if (DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow) is { } owner)
                window.Owner = owner;
            window.ShowDialog();
            return window.Decision;
        }).Task;
    }
}
