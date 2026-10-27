using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>Where a definition in the Workbench comes from.</summary>
public enum DefinitionSource
{
    /// <summary>Made from a local installer: Add from file, or a saved bring-your-own installer.</summary>
    LocalFile,
    /// <summary>Shipped with the application.</summary>
    Shipped,
    /// <summary>Made by the user with a remote version source: Add from web.</summary>
    FromWeb
}

public sealed record OpenDefinitionRow(string ApplicationId, string Name, string Publisher, DefinitionSource Source)
{
    public string SourceText => OpenDefinitionViewModel.SourceText(Source);
}

/// <summary>The Open definition list of the Workbench: every definition of the three sources, with a source filter and a text filter.</summary>
public sealed partial class OpenDefinitionViewModel : ObservableObject
{
    public const string AllSources = "All sources";

    private readonly IReadOnlyList<OpenDefinitionRow> _all;

    public OpenDefinitionViewModel(IEnumerable<WorkbenchApplicationItem> items)
    {
        _all = [.. items.Select(i => new OpenDefinitionRow(ApplicationId: i.ApplicationId, Name: i.DisplayName, Publisher: i.Publisher, Source: SourceOf(i)))];
        Apply();
    }

    public static string SourceText(DefinitionSource source) => source switch
    {
        DefinitionSource.LocalFile => "Local file",
        DefinitionSource.Shipped => "Shipped",
        _ => "From web"
    };

    public static DefinitionSource SourceOf(WorkbenchApplicationItem item) => item.Origin switch
    {
        ApplicationOrigin.Catalog => DefinitionSource.Shipped,
        ApplicationOrigin.Byo => DefinitionSource.LocalFile,
        _ => item.HasRemoteSource || !item.IsJsonDefinition ? DefinitionSource.FromWeb : DefinitionSource.LocalFile
    };

    public IReadOnlyList<string> SourceOptions { get; } =
        [AllSources, SourceText(DefinitionSource.LocalFile), SourceText(DefinitionSource.Shipped), SourceText(DefinitionSource.FromWeb)];

    public ObservableCollection<OpenDefinitionRow> Rows { get; } = [];

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _selectedSource = AllSources;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    private OpenDefinitionRow? _selectedRow;

    public bool CanOpen => SelectedRow is not null;

    partial void OnFilterTextChanged(string value) => Apply();
    partial void OnSelectedSourceChanged(string value) => Apply();

    private void Apply()
    {
        var text = FilterText.Trim();
        var selected = SelectedRow;
        Rows.Clear();
        foreach (var row in _all)
        {
            if (SelectedSource != AllSources && row.SourceText != SelectedSource) continue;
            if (text.Length > 0 && !row.Name.Contains(text, StringComparison.OrdinalIgnoreCase) && !row.Publisher.Contains(text, StringComparison.OrdinalIgnoreCase))
                continue;
            Rows.Add(row);
        }
        SelectedRow = selected is not null && Rows.Contains(selected) ? selected : null;
    }
}
