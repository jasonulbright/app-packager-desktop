namespace ApplicationPackagerUI.Services;

/// <summary>The three answers to an unsaved-changes prompt.</summary>
public enum WorkbenchUnsavedAnswer
{
    Save,
    Discard,
    Cancel
}

/// <summary>The dialogs and pickers the Workbench view model asks its window for.</summary>
public interface IWorkbenchPrompts
{
    Task<WorkbenchUnsavedAnswer> UnsavedAsync(string message);
    Task<string?> NameAsync(string prompt, string value);
    Task<bool> YesNoAsync(string title, string message);
    Task InfoAsync(string title, string message);
    IReadOnlyList<string> PickFiles(string title, string filter, bool multiselect);
    string? PickFolder(string title);
}
