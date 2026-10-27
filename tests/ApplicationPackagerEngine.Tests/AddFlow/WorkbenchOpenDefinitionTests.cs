using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.AddFlow;

public sealed class WorkbenchOpenDefinitionTests
{
    private static WorkbenchApplicationItem Item(string id, ApplicationOrigin origin, string name, bool json = true, bool remote = false) => new()
    {
        ApplicationId = id,
        Origin = origin,
        DisplayName = name,
        Publisher = "Contoso",
        IsJsonDefinition = json,
        HasRemoteSource = remote
    };

    private static readonly WorkbenchApplicationItem[] Items =
    [
        Item("catalog:putty", ApplicationOrigin.Catalog, "PuTTY", remote: true),
        Item("custom:a1", ApplicationOrigin.Custom, "Dropped App"),
        Item("custom:b2", ApplicationOrigin.Custom, "Web App", remote: true),
        Item("byo:0123", ApplicationOrigin.Byo, "Saved Installer", json: false)
    ];

    [Fact]
    public void TheListHasTheThreeSourcesInOneListWithASourceColumn()
    {
        var picker = new OpenDefinitionViewModel(Items);
        Assert.Equal(4, picker.Rows.Count);
        var sources = picker.Rows.ToDictionary(r => r.ApplicationId, r => r.SourceText);
        Assert.Equal("Shipped", sources["catalog:putty"]);
        Assert.Equal("Local file", sources["custom:a1"]);
        Assert.Equal("From web", sources["custom:b2"]);
        Assert.Equal("Local file", sources["byo:0123"]);
        Assert.Equal(["All sources", "Local file", "Shipped", "From web"], picker.SourceOptions);
    }

    [Fact]
    public void TheSourceFilterAndTheTextFilterNarrowTheList()
    {
        var picker = new OpenDefinitionViewModel(Items) { SelectedSource = "Local file" };
        Assert.Equal(["custom:a1", "byo:0123"], picker.Rows.Select(r => r.ApplicationId));

        picker.FilterText = "saved";
        Assert.Equal(["byo:0123"], picker.Rows.Select(r => r.ApplicationId));

        picker.SelectedSource = "All sources";
        picker.FilterText = "contoso";
        Assert.Equal(4, picker.Rows.Count);
    }

    [Fact]
    public void OpenNeedsASelectedRow()
    {
        var picker = new OpenDefinitionViewModel(Items);
        Assert.False(picker.CanOpen);
        picker.SelectedRow = picker.Rows[2];
        Assert.True(picker.CanOpen);
        Assert.Equal("custom:b2", picker.SelectedRow.ApplicationId);
    }

    [Fact]
    public void TheResultPanelOpensTheIdThatTheCatalogGivesAUserDefinition() =>
        Assert.Equal(ApplicationIds.New(ApplicationOrigin.Custom, name: "contoso0001"), WorkbenchApplicationCatalog.ApplicationIdForDefinition("contoso0001"));

    [Fact]
    public void OpenDefinitionAndThePreselectUseTheSameLookup()
    {
        Assert.Same(Items[2], WorkbenchWindowViewModel.FindDefinition(Items, "CUSTOM:b2"));
        Assert.Null(WorkbenchWindowViewModel.FindDefinition(Items, "custom:missing"));
        Assert.Null(WorkbenchWindowViewModel.FindDefinition(Items, null));
    }
}
