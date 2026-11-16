namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The global title checkboxes. Checking Include clears Remove and disables it;
/// Remove is stored only while Include is off. The architecture checkbox is independent.
/// </summary>
public partial class ApplicationTitleOptions : ObservableObject
{
    [ObservableProperty] private bool _includeVersionInTitle;
    [ObservableProperty] private bool _removeVersionFromTitle;
    [ObservableProperty] private bool _appendArchitectureToTitle = true;

    public bool RemoveVersionFromTitleAvailable => !IncludeVersionInTitle;

    partial void OnIncludeVersionInTitleChanged(bool value)
    {
        if (value) RemoveVersionFromTitle = false;
        OnPropertyChanged(nameof(RemoveVersionFromTitleAvailable));
    }

    public void Load(bool include, bool remove, bool appendArchitecture = true)
    {
        IncludeVersionInTitle = include;
        RemoveVersionFromTitle = remove && !include;
        AppendArchitectureToTitle = appendArchitecture;
    }

    /// <summary>The stored pair: Remove is false whenever Include is on.</summary>
    public (bool Include, bool Remove) ToSettings() => (IncludeVersionInTitle, RemoveVersionFromTitle && !IncludeVersionInTitle);
}
