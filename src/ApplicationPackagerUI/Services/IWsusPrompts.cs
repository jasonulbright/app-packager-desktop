namespace ApplicationPackagerUI.Services;

/// <summary>The confirmations, pickers and password prompt the WSUS surfaces ask their window for.</summary>
public interface IWsusPrompts
{
    /// <summary>A Yes/No question; true for Yes.</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>The PFX file to import, or null on cancel.</summary>
    string? PickPfxFile();

    /// <summary>The password typed for a PFX, or null on cancel. The text is never stored.</summary>
    Task<string?> AskPasswordAsync(string prompt);

    /// <summary>Where to save the public certificate, or null on cancel.</summary>
    string? PickCertificateSavePath(string defaultFileName);

    /// <summary>A text file of update ids to load, or null on cancel.</summary>
    string? PickIdFile();

    /// <summary>Opens a web page in the default browser.</summary>
    void OpenUrl(string url);
}
