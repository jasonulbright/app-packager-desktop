namespace ApplicationPackagerUI.Models;

/// <summary>What a sidebar entry does when the operator selects it.</summary>
public enum SidebarItemKind
{
    /// <summary>Shows a page in the main window.</summary>
    Page,

    /// <summary>Opens a window of this application.</summary>
    Window,

    /// <summary>Starts a separate tool.</summary>
    Tool
}

/// <summary>
/// One sidebar entry. A Page entry names its page type; a Window or Tool entry has an
/// action that receives the application's service provider. A pinned entry is always
/// shown and always first.
/// </summary>
public sealed record SidebarItemDefinition(string Id, string Title, string Symbol, SidebarItemKind Kind)
{
    public Type? PageType { get; init; }

    public Action<IServiceProvider>? Activate { get; init; }

    public bool IsPinned { get; init; }
}

/// <summary>
/// The saved sidebar: every entry id in sidebar order, and the ids of the hidden entries.
/// Either list can be null in a file edited by hand.
/// </summary>
public sealed class SidebarLayout
{
    public string[]? Order { get; set; } = [];

    public string[]? Hidden { get; set; } = [];
}
