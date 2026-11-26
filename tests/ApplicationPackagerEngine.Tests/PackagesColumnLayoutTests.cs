using System.Text.Json;
using ApplicationPackagerUI.Helpers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Overhaul V1 phase 3c (Q-126): the saved column layout of the Packages grid.</summary>
public sealed class PackagesColumnLayoutTests
{
    private static readonly string[] Defaults = ["Application", "Vendor", "Latest", "Status"];

    private static PackagesColumnLayout Saved(params string[] ids) => new() { Columns = ids.Select(i => new PackagesColumnState { Id = i }).ToList() };

    [Fact]
    public void NoSavedLayoutKeepsTheOrderOfThePage()
    {
        Assert.Equal(Defaults, PackagesColumnLayout.Order(Defaults, null));
    }

    [Fact]
    public void TheSavedOrderWinsAndAnUnknownOrDoubledIdIsDropped()
    {
        Assert.Equal(["Status", "Application", "Vendor", "Latest"],
            PackagesColumnLayout.Order(Defaults, Saved("Status", "Gone", "Application", "Status", "Vendor", "Latest")));
    }

    [Fact]
    public void AColumnThatTheSavedLayoutDoesNotNameGoesNextToItsNeighbor()
    {
        // "Vendor" is new: it goes after "Application", its neighbor on the left in the page order.
        Assert.Equal(["Latest", "Application", "Vendor", "Status"], PackagesColumnLayout.Order(Defaults, Saved("Latest", "Application", "Status")));
        // The first column of the page order has no neighbor on its left, so it goes first.
        Assert.Equal(["Application", "Status", "Vendor", "Latest"], PackagesColumnLayout.Order(Defaults, Saved("Status", "Vendor", "Latest")));
    }

    [Fact]
    public void OnlyAUsableWidthIsApplied()
    {
        var layout = new PackagesColumnLayout
        {
            Columns = [new() { Id = "a", Width = 220 }, new() { Id = "b", Width = 0 }, new() { Id = "c", Width = -4 }, new() { Id = "d", Width = 90000 }]
        };
        Assert.Equal(220, PackagesColumnLayout.WidthOf(layout, "a"));
        foreach (var id in new[] { "b", "c", "d", "e" }) Assert.Null(PackagesColumnLayout.WidthOf(layout, id));
        Assert.Null(PackagesColumnLayout.WidthOf(null, "a"));
    }

    [Theory]
    [InlineData("{\"Columns\":null}")]
    [InlineData("{\"Columns\":[null,{\"Id\":null,\"Width\":90}]}")]
    public void ADamagedLayoutKeepsTheOrderOfThePage(string json)
    {
        var layout = JsonSerializer.Deserialize<PackagesColumnLayout>(json)!;
        Assert.Equal(Defaults, PackagesColumnLayout.Order(Defaults, layout));
        Assert.Null(PackagesColumnLayout.WidthOf(layout, "Vendor"));
    }

    [Fact]
    public void TheLayoutSurvivesTheSettingsFile()
    {
        var layout = new PackagesColumnLayout { Columns = [new() { Id = "Latest", Width = 150 }], SortId = "Latest", SortDescending = true };
        var read = JsonSerializer.Deserialize<PackagesColumnLayout>(JsonSerializer.Serialize(layout))!;
        Assert.Equal("Latest", Assert.Single(read.Columns).Id);
        Assert.Equal(150, read.Columns[0].Width);
        Assert.Equal("Latest", read.SortId);
        Assert.True(read.SortDescending);
    }
}
