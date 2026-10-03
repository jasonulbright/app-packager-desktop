using ApplicationPackagerUI.Services;
using Wpf.Ui.Appearance;

namespace ApplicationPackagerEngine.Tests;

public class ThemeApplierTests
{
    [Theory]
    [InlineData("Dark", ApplicationTheme.Dark)]
    [InlineData("Light", ApplicationTheme.Light)]
    [InlineData("light", ApplicationTheme.Light)]
    [InlineData(" Dark ", ApplicationTheme.Dark)]
    public void AStoredNameSelectsItsTheme(string stored, ApplicationTheme expected)
    {
        Assert.Equal(expected, ThemeApplier.Resolve(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HighContrast")]
    [InlineData("Unknown")]
    [InlineData("1")]
    [InlineData("Blue")]
    public void AnyOtherStoredValueSelectsNoTheme(string? stored)
    {
        Assert.Null(ThemeApplier.Resolve(stored));
    }
}
