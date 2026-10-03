using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A throwaway workbench data root plus scratch folders for one test class.</summary>
public sealed class WorkbenchTestRoot : IDisposable
{
    public WorkbenchTestRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "apwb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Store = new WorkbenchStore(Path.Combine(Root, "data"));
    }

    public string Root { get; }
    public WorkbenchStore Store { get; }

    public string NewFolder(string? name = null)
    {
        var path = Path.Combine(Root, name ?? Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public string NewFile(string path, string content = "test")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>Saves a new profile and makes it the application's active profile.</summary>
    public WorkbenchProfile NewProfile(string applicationId, string name = "Managed") =>
        Store.SaveProfile(WorkbenchProfile.CreateNew(applicationId, name: name), setActive: true);

    public static JsonObject Json(string json) => (JsonObject)WorkbenchJson.Parse(json)!;

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Tests that read or write the process environment run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkbenchEnvironmentCollection
{
    public const string Name = "Workbench process environment";
}
