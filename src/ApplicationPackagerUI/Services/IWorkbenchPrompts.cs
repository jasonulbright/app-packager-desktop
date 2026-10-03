using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Services;

/// <summary>The three answers to an unsaved-changes prompt.</summary>
public enum WorkbenchUnsavedAnswer
{
    Save,
    Discard,
    Cancel
}

/// <summary>The reviewed values of an accepted replacement installer.</summary>
public sealed record ByoReplacementReview(DroppedInstallerOverrides Overrides, bool Confirmed);

/// <summary>The dialogs and pickers the Workbench view model asks its window for.</summary>
public interface IWorkbenchPrompts
{
    Task<WorkbenchUnsavedAnswer> UnsavedAsync(string message);
    Task<string?> NameAsync(string prompt, string value);
    Task<bool> YesNoAsync(string title, string message);
    Task InfoAsync(string title, string message);
    IReadOnlyList<string> PickFiles(string title, string filter, bool multiselect);
    string? PickFolder(string title);

    /// <summary>The path to write, or null when the operator cancelled.</summary>
    string? PickSaveFile(string title, string filter, string fileName) => null;

    /// <summary>
    /// Shows the identity differences and the review of a replacement installer; null when
    /// the operator cancelled or the review is incomplete.
    /// </summary>
    Task<ByoReplacementReview?> ReviewReplacementAsync(DroppedInstallerAnalysis analysis, IReadOnlyList<ByoIdentityChange> changes,
        int revision, int previousRevision, string displayName, string publisher) =>
        Task.FromResult<ByoReplacementReview?>(null);
}
