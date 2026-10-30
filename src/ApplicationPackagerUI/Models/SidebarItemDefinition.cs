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
/// shown and always first. An entry stays inside its group: no move takes it past an entry
/// of another group.
/// </summary>
public sealed record SidebarItemDefinition(string Id, string Title, string Symbol, SidebarItemKind Kind)
{
    public Type? PageType { get; init; }

    public Action<IServiceProvider>? Activate { get; init; }

    public bool IsPinned { get; init; }

    /// <summary>The id of a captioned group; null puts the entry in the group without a caption.</summary>
    public string? Group { get; init; }
}

/// <summary>
/// A captioned group of sidebar entries. The caption is the uppercase text the sidebar shows;
/// the automation name is what a screen reader reads for the caption.
/// </summary>
public sealed record SidebarGroup(string Id, string Caption, string AutomationName);

/// <summary>
/// The saved sidebar: every entry id in sidebar order, and the ids of the hidden entries.
/// Either list can be null in a file edited by hand.
/// </summary>
public sealed class SidebarLayout
{
    public string[]? Order { get; set; } = [];

    public string[]? Hidden { get; set; } = [];
}
