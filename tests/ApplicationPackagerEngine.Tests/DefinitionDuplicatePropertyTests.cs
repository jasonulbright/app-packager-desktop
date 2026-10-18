using System.Text.Json;

namespace ApplicationPackagerEngine.Tests;

public sealed class DefinitionDuplicatePropertyTests
{
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// The duplicate names in one JSON text, compared without case: the case-insensitive
    /// readers reject them, and a case-sensitive reader keeps only one of the values.
    /// </summary>
    public static IReadOnlyList<string> DuplicateProperties(string json)
    {
        var found = new List<string>();
        try
        {
            using var strict = JsonDocument.Parse(json, Strict);
        }
        catch (JsonException ex)
        {
            found.Add(ex.Message);
        }
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement, "$", found);
        return found;
    }

    private static void Walk(JsonElement element, string path, List<string> found)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) Walk(item, $"{path}[{index++}]", found);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) found.Add($"{path}.{property.Name}");
            Walk(property.Value, $"{path}.{property.Name}", found);
        }
    }

    [Fact]
    public void NoShippedDefinitionHasADuplicateProperty()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Definitions");
        var files = Directory.GetFiles(folder, "*.json");
        Assert.NotEmpty(files);
        var failures = files
            .SelectMany(file => DuplicateProperties(File.ReadAllText(file)).Select(d => $"{Path.GetFileName(file)}: {d}"))
            .ToList();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("""{ "InstallerType": "EXE", "InstallerType": "MSI" }""", "$.InstallerType")]
    [InlineData("""{ "ArpName": "a", "arpname": "b" }""", "$.arpname")]
    [InlineData("""{ "Detection": { "Type": "a", "Type": "b" } }""", "$.Detection.Type")]
    [InlineData("""{ "RunningProcess": [ { "Name": "a", "NAME": "b" } ] }""", "$.RunningProcess[0].NAME")]
    public void TheCheckFindsADuplicateAtAnyDepthAndInAnyCase(string json, string path)
    {
        Assert.Contains(path, DuplicateProperties(json));
    }

    [Fact]
    public void TheCheckPassesAFileWithoutDuplicates()
    {
        Assert.Empty(DuplicateProperties("""{ "InstallerType": "MSI", "Detection": { "Type": "a" }, "List": [ { "Type": "b" } ] }"""));
    }
}
