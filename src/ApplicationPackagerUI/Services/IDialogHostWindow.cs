namespace ApplicationPackagerUI.Services;

/// <summary>A window that hosts its own content dialogs, so a prompt opens over that window.</summary>
public interface IDialogHostWindow
{
    ContentDialogHost DialogHost { get; }
}
