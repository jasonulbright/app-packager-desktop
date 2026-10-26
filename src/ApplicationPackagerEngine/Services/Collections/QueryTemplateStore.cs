using System.Reflection;
using System.Text.Json;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>The Collections folders under the per-user data root.</summary>
internal static class CollectionsFolders
{
    public const string FolderName = "Collections";

    public static string Root() => Path.Combine(AppDataPaths.Root, FolderName);
}

/// <summary>
/// The shipped query templates, read from embedded resources, and the user templates: every
/// *.json file directly in the user folder, holding one template object or an array of them.
/// A user file that is a reparse point, larger than <see cref="MaxUserFileBytes"/> or not
/// valid JSON is skipped and named in <see cref="Message"/>; a user folder that is a junction
/// or symbolic link is not read. Unknown properties are ignored. User templates get ids of
/// their own, so they never replace a shipped template.
/// </summary>
public sealed class QueryTemplateStore : IQueryTemplateStore
{
    public const string LogArea = "Collections";

    public const long MaxUserFileBytes = 4 * 1024 * 1024;

    private const string ResourcePrefix = "ApplicationPackagerEngine.Services.Collections.Templates.";

    private static readonly Lazy<IReadOnlyList<QueryTemplate>> Shipped = new(LoadShipped);

    private readonly string? _folder;
    private readonly IAppLog? _log;
    private readonly object _gate = new();
    private string? _message;

    public QueryTemplateStore(string? folder = null, IAppLog? log = null)
    {
        _folder = folder;
        _log = log;
    }

    public static string DefaultFolder() => Path.Combine(CollectionsFolders.Root(), "Templates");

    public string? Message
    {
        get { lock (_gate) return _message; }
    }

    public IReadOnlyList<QueryTemplate> All()
    {
        lock (_gate)
        {
            var result = Shipped.Value.ToList();
            var skipped = new List<string>();
            var user = ReadUserTemplates(skipped);
            result.AddRange(user.OrderBy(t => t.Category, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase));
            _message = skipped.Count == 0
                ? null
                : $"{(skipped.Count == 1 ? "1 user template file was" : $"{skipped.Count} user template files were")} skipped: {string.Join("; ", skipped)}.";
            _log?.Info(LogArea, $"Loaded {Shipped.Value.Count} shipped and {user.Count} user query templates; {skipped.Count} user files skipped.");
            return result;
        }
    }

    private List<QueryTemplate> ReadUserTemplates(List<string> skipped)
    {
        var result = new List<QueryTemplate>();
        var folder = _folder ?? DefaultFolder();
        string[] files;
        try
        {
            if (!Directory.Exists(folder)) return result;
            if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, _folder is null)))
            {
                skipped.Add("the user templates folder is a junction or symbolic link, so it was not read");
                _log?.Warning(LogArea, "The user query templates folder is a junction or symbolic link; it is not read.");
                return result;
            }
            files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            skipped.Add("the user templates folder could not be read");
            _log?.Warning(LogArea, $"The user query templates folder could not be read ({ex.GetType().Name}).");
            return result;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            try
            {
                if (!Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { skipped.Add($"{name} (a link)"); continue; }
                if (info.Length > MaxUserFileBytes) { skipped.Add($"{name} (larger than 4 MB)"); continue; }
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
                var items = document.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => document.RootElement.EnumerateArray().ToList(),
                    JsonValueKind.Object => [document.RootElement],
                    _ => []
                };
                var before = result.Count;
                for (var i = 0; i < items.Count; i++)
                    if (Convert(items[i], $"user:{Path.GetFileNameWithoutExtension(name)}:{i}", isShipped: false) is { } template)
                        result.Add(template);
                if (result.Count == before) skipped.Add($"{name} (no template with a name and a query)");
            }
            catch (JsonException)
            {
                skipped.Add($"{name} (not valid JSON)");
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                skipped.Add($"{name} (could not be read)");
                _log?.Warning(LogArea, $"A user query template file could not be read ({ex.GetType().Name}).");
            }
        }
        return result;
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static string Text(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            foreach (var property in item.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString() ?? "";
        return "";
    }

    private static JsonElement? Property(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    /// <summary>A template object as the contract's model; null when it has no name or no query.</summary>
    internal static QueryTemplate? Convert(JsonElement item, string id, bool isShipped)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var name = Text(item, "Name").Trim();
        var expression = Text(item, "Query", "Expression").Trim();
        if (name.Length == 0 || expression.Length == 0) return null;

        var details = new List<QueryTemplateParameter>();
        if (Property(item, "Parameters") is { ValueKind: JsonValueKind.Array } parameters)
        {
            foreach (var p in parameters.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                    details.Add(new QueryTemplateParameter(p.GetString()!, p.GetString()!.Trim('{', '}'), "", ""));
                else if (p.ValueKind == JsonValueKind.Object && Text(p, "Placeholder") is { Length: > 0 } placeholder)
                    details.Add(new QueryTemplateParameter(placeholder, Text(p, "Label") is { Length: > 0 } label ? label : placeholder.Trim('{', '}'),
                        Text(p, "HelpText"), Text(p, "DefaultValue")));
            }
        }
        details = details.Where(d => expression.Contains(d.Placeholder, StringComparison.Ordinal))
            .GroupBy(d => d.Placeholder, StringComparer.Ordinal).Select(g => g.First()).ToList();
        return new QueryTemplate(id, name, Text(item, "Category").Trim() is { Length: > 0 } category ? category : "Other", expression,
            details.Select(d => d.Placeholder).ToList(), Text(item, "Description"), isShipped, details, Text(item, "LimitingCollection"));
    }

    private static IReadOnlyList<QueryTemplate> LoadShipped()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var result = new List<QueryTemplate>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var document = JsonDocument.Parse(stream);
            var file = resource[ResourcePrefix.Length..^".json".Length];
            var index = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (Convert(item, $"shipped:{file}:{index}", isShipped: true) is { } template) result.Add(template);
                index++;
            }
        }
        return result;
    }
}
