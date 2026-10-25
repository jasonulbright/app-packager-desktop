namespace ApplicationPackagerUI.Models;

/// <summary>The last tab, filters and dialog size of the Collection and Compliance page.</summary>
public sealed class CollectionComplianceSettings
{
    /// <summary>0 Collections, 1 WQL Editor, 2 Templates, 3 Site Queries.</summary>
    public int SelectedTab { get; set; }

    /// <summary>0 Properties, 1 Direct Members, 2 Query Rules, 3 Include Rules, 4 Exclude Rules.</summary>
    public int DetailTab { get; set; }

    /// <summary>True when the details panel under the Collections grid is hidden.</summary>
    public bool DetailsHidden { get; set; }

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

    /// <summary>0 Collections, 1 Compliance.</summary>
    public int Section { get; set; }

    /// <summary>The choices of the Compliance section; null in files written before it existed.</summary>
    public ComplianceSettings? Compliance { get; set; }
}

/// <summary>The last tab, filters and panel state of the Compliance section.</summary>
public sealed class ComplianceSettings
{
    /// <summary>0 Catalog, 1 Live CI/CB, 2 Editor, 3 Auditing.</summary>
    public int SelectedTab { get; set; }

    public string CatalogText { get; set; } = "";

    /// <summary>Empty for every subcategory.</summary>
    public string CatalogSubcategory { get; set; } = "";

    public string CatalogSeverity { get; set; } = "";
    public string CatalogStatus { get; set; } = "";
    public string CatalogOs { get; set; } = "";

    /// <summary>A CVE or a plugin ID the entry must name.</summary>
    public string CatalogReference { get; set; } = "";

    public bool CatalogDetailsHidden { get; set; }

    public string LiveText { get; set; } = "";
    public bool LiveDeploymentsHidden { get; set; }

    /// <summary>0 Drift, 1 Compliance.</summary>
    public int AuditingTab { get; set; }

    public string AuditingText { get; set; } = "";

    /// <summary>Empty for every state, else Current, Behind, Untagged or Unknown.</summary>
    public string DriftState { get; set; } = "";

    public bool OnlyStale { get; set; }
}
