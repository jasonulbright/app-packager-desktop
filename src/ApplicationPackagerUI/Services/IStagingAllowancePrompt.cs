namespace ApplicationPackagerUI.Services;

/// <summary>The acknowledgement window of "Allow this folder anyway".</summary>
public interface IStagingAllowancePrompt
{
    /// <summary>Shows the reason and asks for the checked acknowledgement. True only when the user checked the box and chose Accept.</summary>
    bool Confirm(string folder, string reason);
}
