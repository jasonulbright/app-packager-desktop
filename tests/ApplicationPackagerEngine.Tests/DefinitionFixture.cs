using System.Text.Json;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Loads a shipped definition with the same options the discovery service
/// uses, so a test reads what the app reads.
/// </summary>
internal static class DefinitionFixture
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static AppDefinition Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Definitions", fileName);
        Assert.True(File.Exists(path), $"Definition not found: {path}");
        return JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Definition deserialized to null: {fileName}");
    }
}
