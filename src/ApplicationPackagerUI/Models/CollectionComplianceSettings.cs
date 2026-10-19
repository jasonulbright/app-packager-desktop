namespace ApplicationPackagerUI.Models;

/// <summary>The last tab, filters and dialog size of the Collection and Compliance page.</summary>
public sealed class CollectionComplianceSettings
{
    /// <summary>0 Collections, 1 WQL Editor, 2 Templates, 3 Site Queries.</summary>
    public int SelectedTab { get; set; }

    /// <summary>0 Properties, 1 Direct Members, 2 Query Rules, 3 Include Rules, 4 Exclude Rules.</summary>
    public int DetailTab { get; set; }

    public string TextFilter { get; set; } = "";

    /// <summary>Empty for every collection, else BuiltIn or Custom.</summary>
    public string TypeFilter { get; set; } = "";

    public bool OnlyEmpty { get; set; }
    public bool OnlyWithDirectMembers { get; set; }
    public bool OnlyWithQueryRules { get; set; }

    /// <summary>Empty for every folder.</summary>
    public string FolderFilter { get; set; } = "";

    /// <summary>Empty for every category.</summary>
    public string TemplateCategory { get; set; } = "";

    public double EditorWidth { get; set; }
    public double EditorHeight { get; set; }
}
