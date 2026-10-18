using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Services;

/// <summary>The definitions Discover unmanaged software matches against inventory, hidden ones included.</summary>
public static class DiscoveryPackagerSource
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// One packager per definition with a vendor and an application. The Add/Remove Programs
    /// tags come from the definition file itself; a file that cannot be read gives empty tags.
    /// </summary>
    public static IReadOnlyList<DiscoveryPackager> FromDefinitions(IEnumerable<AppDefinition> definitions, Action<string>? log = null)
    {
        var packagers = new List<DiscoveryPackager>();
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Vendor)
                || string.IsNullOrWhiteSpace(definition.Application)) continue;
            var (arpName, arpVersion, arpArch) = ReadArpTags(definition.SourcePath, log);
            packagers.Add(new DiscoveryPackager(definition.Id, definition.Vendor, definition.Application,
                string.IsNullOrWhiteSpace(definition.CMName) ? definition.Application : definition.CMName,
                arpName, arpVersion, arpArch));
        }
        return packagers;
    }

    /// <summary>The shipped and user definitions, as the Packages grid resolves them.</summary>
    public static Task<IReadOnlyList<DiscoveryPackager>> LoadAsync(Action<string>? log, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DiscoveryPackager>>(() => FromDefinitions(PackagerDiscoveryService.LoadEffectiveDefinitions(
            IconPackService.BundledDefinitionsDirectory(), AppDataPaths.UserDefinitionsRoot, ct), log), ct);

    /// <summary>
    /// The case-insensitive object throws ArgumentException on a name that appears twice in
    /// any case; such a file, like one that is not JSON, gives empty tags and is never offered.
    /// </summary>
    private static (string ArpName, string? ArpVersion, string? ArpArch) ReadArpTags(string path, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("", null, null);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), NodeOptions) is not JsonObject root) return ("", null, null);
            return (Text(root, "ArpName") ?? "", Text(root, "ArpVersion"), Text(root, "ArpArch"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            log?.Invoke($"{DiscoveryRunner.LogPrefix}the definition file {Path.GetFileName(path)} could not be read and is skipped.");
            return ("", null, null);
        }
    }

    private static string? Text(JsonObject root, string name) =>
        root.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
