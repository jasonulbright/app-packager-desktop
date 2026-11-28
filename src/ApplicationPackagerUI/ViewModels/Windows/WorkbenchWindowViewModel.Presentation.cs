namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>
/// What the Workbench shows in the words and the fields of the mock: an inherited value stands as the gray text of an empty field, a field with
/// text holds an override, and the side panel names the identity, the source and each validation in a few words.
/// </summary>
public partial class WorkbenchWindowViewModel
{
    private const string InheritedStart = "Inherited: ";

    private static bool IsInherited(string source) => source.Length == 0 || source.StartsWith(InheritedStart, StringComparison.Ordinal);

    private static string Hint(string source) => source.Replace("(none)", "none", StringComparison.Ordinal);

    public string DisplayNameInput { get => IsInherited(DisplayNameSource) ? "" : DisplayName; set { if (value != DisplayNameInput) DisplayName = value; } }

    public string DisplayNameHint => Hint(DisplayNameSource);

    public string PublisherInput { get => IsInherited(PublisherSource) ? "" : Publisher; set { if (value != PublisherInput) Publisher = value; } }

    public string PublisherHint => Hint(PublisherSource);

    public string DescriptionInput { get => IsInherited(DescriptionSource) ? "" : Description; set { if (value != DescriptionInput) Description = value; } }

    public string DescriptionHint => Hint(DescriptionSource);

    /// <summary>"7-Zip catalog application": the identity that outputs and vendor discovery keep.</summary>
    public string IdentityShort
    {
        get
        {
            if (SelectedApplication is null) return "";
            var name = SelectedApplication.DisplayName;
            var bracket = name.LastIndexOf(" (", StringComparison.Ordinal);
            return (bracket > 0 ? name[..bracket] : name) + " " + (SourceTypeText.StartsWith("Catalog", StringComparison.OrdinalIgnoreCase) ? "catalog application" : "application");
        }
    }

    /// <summary>The folder and the file of the definition; the whole path is the tooltip.</summary>
    public string ProvenanceShort
    {
        get
        {
            var path = ProvenanceText;
            if (!path.Contains(System.IO.Path.DirectorySeparatorChar)) return path;
            var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path));
            return string.IsNullOrEmpty(folder) ? System.IO.Path.GetFileName(path) : folder + System.IO.Path.DirectorySeparatorChar + System.IO.Path.GetFileName(path);
        }
    }

    /// <summary>The two characters in the icon square while the application has no icon.</summary>
    public string IconMark => Helpers.PackagesCells.MarkOf(SelectedApplication?.DisplayName);

    private static string Chip(string text) => text.Replace(": Not validated", " not validated", StringComparison.Ordinal);

    public string ChipContentShort => Chip(ChipContent);

    public string ChipConfigMgrShort => Chip(ChipConfigMgr);

    public string ChipIntuneShort => Chip(ChipIntune);

    /// <summary>The properties that the presentation reads: a change of one of them redraws the words made from it.</summary>
    private static readonly Dictionary<string, string[]> Drawn = new(StringComparer.Ordinal)
    {
        [nameof(DisplayName)] = [nameof(DisplayNameInput)],
        [nameof(DisplayNameSource)] = [nameof(DisplayNameInput), nameof(DisplayNameHint)],
        [nameof(Publisher)] = [nameof(PublisherInput)],
        [nameof(PublisherSource)] = [nameof(PublisherInput), nameof(PublisherHint)],
        [nameof(Description)] = [nameof(DescriptionInput)],
        [nameof(DescriptionSource)] = [nameof(DescriptionInput), nameof(DescriptionHint)],
        [nameof(IdentityText)] = [nameof(IdentityShort)],
        [nameof(SourceTypeText)] = [nameof(IdentityShort)],
        [nameof(SelectedApplication)] = [nameof(IdentityShort), nameof(IconMark)],
        [nameof(ProvenanceText)] = [nameof(ProvenanceShort)],
        [nameof(ChipContent)] = [nameof(ChipContentShort)],
        [nameof(ChipConfigMgr)] = [nameof(ChipConfigMgrShort)],
        [nameof(ChipIntune)] = [nameof(ChipIntuneShort)],
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is null || !Drawn.TryGetValue(e.PropertyName, out var also)) return;
        foreach (var name in also) base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
