using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// File store for workbench applications, profiles, assets, run snapshots and drafts.
/// The on-disk layout matches the one a PS1 packager child reads through
/// APP_PACKAGER_WORKBENCH_ROOT:
/// applications\&lt;key&gt;\application.json,
/// applications\&lt;key&gt;\profiles\&lt;ProfileId&gt;\profile.json and assets\&lt;AssetId&gt;\,
/// runs\&lt;BuildId&gt;.json, drafts\&lt;key&gt;\&lt;ProfileId&gt;.draft.json.
/// </summary>
public sealed partial class WorkbenchStore
{
    [GeneratedRegex(@"^package-.*\.(?:ps1|notps1)$", RegexOptions.IgnoreCase)]
    private static partial Regex PackagerScriptPattern();

    public WorkbenchStore(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("A workbench data root is required.", nameof(dataRoot));
        DataRoot = Path.GetFullPath(dataRoot);
    }

    /// <summary>Opens the store at the resolved data root (see <see cref="WorkbenchPaths.ResolveDataRoot"/>).</summary>
    public static WorkbenchStore Open(string? preference = null) => new(WorkbenchPaths.ResolveDataRoot(preference));

    public string DataRoot { get; }
    public string ApplicationsRoot => Path.Combine(DataRoot, "applications");
    public string RunsRoot => Path.Combine(DataRoot, "runs");
    public string DraftsRoot => Path.Combine(DataRoot, "drafts");

    /// <summary>The user-writable folder for user-authored JSON definitions (custom: origin).</summary>
    public string CustomDefinitionsRoot => Path.Combine(DataRoot, "definitions");

    public string GetApplicationFolder(string applicationId, bool create = false)
    {
        var folder = Path.Combine(ApplicationsRoot, ApplicationIds.ToKey(applicationId));
        if (create) Directory.CreateDirectory(folder);
        return folder;
    }

    // ── Application definitions ─────────────────────────────────

    /// <summary>Reads application.json, or returns the unsaved default definition.</summary>
    public WorkbenchApplicationDefinition GetApplicationDefinition(string applicationId)
    {
        var path = Path.Combine(GetApplicationFolder(applicationId), "application.json");
        var stored = ReadJson<WorkbenchApplicationDefinition>(path);
        if (stored is not null)
        {
            stored.Persisted = true;
            if (string.IsNullOrWhiteSpace(stored.ActiveProfileId)) stored.ActiveProfileId = ApplicationIds.DefaultProfileId;
            stored.Sources ??= [];
            return stored;
        }
        return new WorkbenchApplicationDefinition
        {
            ApplicationId = applicationId,
            Origin = ApplicationIds.OriginOf(applicationId),
            UpdatePolicy = ApplicationIds.IsByo(applicationId) ? "Manual" : "Discovered",
            ActiveProfileId = ApplicationIds.DefaultProfileId,
            Persisted = false
        };
    }

    /// <summary>Writes application.json atomically and returns the stored definition.</summary>
    public WorkbenchApplicationDefinition SaveApplicationDefinition(WorkbenchApplicationDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ApplicationId))
            throw new InvalidOperationException("An application definition requires an ApplicationId.");
        var folder = GetApplicationFolder(definition.ApplicationId);
        if (definition.SchemaVersion <= 0) definition.SchemaVersion = WorkbenchApplicationDefinition.CurrentSchemaVersion;
        if (definition.SchemaVersion > WorkbenchApplicationDefinition.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Application definition schema {definition.SchemaVersion} is newer than this build supports ({WorkbenchApplicationDefinition.CurrentSchemaVersion}).");
        if (string.IsNullOrWhiteSpace(definition.ActiveProfileId)) definition.ActiveProfileId = ApplicationIds.DefaultProfileId;

        WorkbenchJson.Write(Path.Combine(folder, "application.json"), definition);
        return GetApplicationDefinition(definition.ApplicationId);
    }

    /// <summary>
    /// Lists every application the workbench can edit: shipped JSON definitions and
    /// PS1 packagers (catalog), user-authored JSON definitions (custom), and every
    /// persisted BYO application. A stored definition supplies the display metadata;
    /// a discovered source without one is still listed. The first id seen wins.
    /// </summary>
    public IReadOnlyList<WorkbenchApplication> DiscoverApplications(WorkbenchDiscoveryRoots? roots = null)
    {
        roots ??= new WorkbenchDiscoveryRoots();
        var results = new List<WorkbenchApplication>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDefinitions(string? root, ApplicationOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            foreach (var file in Directory.GetFiles(root, "*.json").OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                var (id, name) = ReadDefinitionIdentity(file);
                if (string.IsNullOrWhiteSpace(id)) continue;
                string applicationId;
                try { applicationId = ApplicationIds.New(origin, name: id); }
                catch (ArgumentException) { continue; }
                if (!ApplicationIds.IsValid(applicationId) || !seen.Add(applicationId)) continue;
                results.Add(Describe(applicationId, origin, name ?? Path.GetFileNameWithoutExtension(file), file, runnable: true));
            }
        }

        void AddScripts(string? root, ApplicationOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            foreach (var file in Directory.GetFiles(root)
                         .Where(f => PackagerScriptPattern().IsMatch(Path.GetFileName(f)))
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                var applicationId = ApplicationIds.New(origin, path: file);
                if (!ApplicationIds.IsValid(applicationId) || !seen.Add(applicationId)) continue;
                var runnable = !string.Equals(Path.GetExtension(file), ".notps1", StringComparison.OrdinalIgnoreCase);
                results.Add(Describe(applicationId, origin, Path.GetFileNameWithoutExtension(file), file, runnable));
            }
        }

        AddDefinitions(roots.CatalogDefinitionsRoot, ApplicationOrigin.Catalog);
        AddScripts(roots.PackagersRoot, ApplicationOrigin.Catalog);
        AddDefinitions(roots.CustomDefinitionsRoot ?? CustomDefinitionsRoot, ApplicationOrigin.Custom);

        if (Directory.Exists(ApplicationsRoot))
        {
            foreach (var folder in Directory.GetDirectories(ApplicationsRoot)
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                var key = Path.GetFileName(folder);
                if (!key.StartsWith("byo_", StringComparison.OrdinalIgnoreCase)) continue;
                var applicationId = ApplicationIds.FromKey(key);
                if (!ApplicationIds.IsValid(applicationId) || !seen.Add(applicationId)) continue;
                var definition = GetApplicationDefinition(applicationId);
                results.Add(new WorkbenchApplication(
                    applicationId, key, ApplicationOrigin.Byo, definition.DisplayName ?? "", definition.Publisher,
                    SourcePath: null, Runnable: true, definition.ActiveProfileId,
                    string.IsNullOrWhiteSpace(definition.UpdatePolicy) ? "Manual" : definition.UpdatePolicy,
                    Persisted: true));
            }
        }
        return results;
    }

    private WorkbenchApplication Describe(string applicationId, ApplicationOrigin origin, string fallbackName, string sourcePath, bool runnable)
    {
        var definition = GetApplicationDefinition(applicationId);
        return new WorkbenchApplication(
            applicationId, ApplicationIds.ToKey(applicationId), origin,
            string.IsNullOrWhiteSpace(definition.DisplayName) ? fallbackName : definition.DisplayName,
            definition.Publisher, sourcePath, runnable, definition.ActiveProfileId, definition.UpdatePolicy,
            definition.Persisted);
    }

    private static (string? Id, string? Name) ReadDefinitionIdentity(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            string? id = null, name = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                if (string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase))
                    id ??= property.Value.GetString();
                else if (string.Equals(property.Name, "Application", StringComparison.OrdinalIgnoreCase))
                    name ??= property.Value.GetString();
            }
            return (id, name);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonSerializer.Deserialize<T>(text, WorkbenchJson.FileOptions);
    }
}
