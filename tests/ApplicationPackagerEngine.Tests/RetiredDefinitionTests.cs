namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A product dropped from the catalog leaves no definition behind; a copy
/// that survives an update keeps offering a package nobody maintains.
/// </summary>
public class RetiredDefinitionTests
{
    [Theory]
    [InlineData("burnaware.json", "fdf12a6e2305720d")]
    public void TheRetiredDefinitionDoesNotShip(string fileName, string id)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Definitions");

        Assert.False(File.Exists(Path.Combine(folder, fileName)));
        Assert.DoesNotContain(Directory.GetFiles(folder, "*.json"),
            path => DefinitionFixture.Load(Path.GetFileName(path)).Id == id);
    }
}
