using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The shipped catalog: one JSON entry and one builder script per entry, both embedded in
/// this assembly. Entries are validated against the catalog schema; an entry that fails, or
/// whose builder is missing, is left out and named in <see cref="Message"/>. There is no
/// other source of entries: a builder runs against the production site, so only the
/// embedded, hash-checked scripts run.
/// </summary>
public sealed partial class CatalogService : ICatalogService
{
    public const string LogArea = "Compliance";

    internal const string ResourcePrefix = "ApplicationPackagerEngine.Services.Compliance.Catalog.";
    internal const string EntryPrefix = ResourcePrefix + "Entries.";
    internal const string BuilderPrefix = ResourcePrefix + "Builders.";

    private readonly Lazy<(IReadOnlyList<CatalogEntry> Entries, string? Message)> _loaded;
    private readonly Func<string, Stream?> _open;

    public CatalogService(IAppLog? log = null) : this(log, null, null) { }

    /// <param name="names">The resource names to read; null reads the names embedded in this assembly.</param>
    /// <param name="open">Opens a resource by name; null opens the embedded resource.</param>
    internal CatalogService(IAppLog? log, IReadOnlyList<string>? names, Func<string, Stream?>? open)
    {
        _open = open ?? (name => typeof(CatalogService).Assembly.GetManifestResourceStream(name));
        var resourceNames = names ?? typeof(CatalogService).Assembly.GetManifestResourceNames();
        _loaded = new Lazy<(IReadOnlyList<CatalogEntry>, string?)>(() => Load(resourceNames, log));
    }

    public IReadOnlyList<CatalogEntry> All() => _loaded.Value.Entries;

    public CatalogEntry? Find(string id) =>
        All().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

    public string? Message => _loaded.Value.Message;

    /// <summary>The bytes of the entry's embedded builder; null when the resource is missing.</summary>
    internal byte[]? ReadBuilder(CatalogEntry entry)
    {
        using var stream = _open(entry.BuilderResource);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [GeneratedRegex(@"^New-[A-Za-z0-9]+\.ps1$")]
    private static partial Regex BuilderNameRegex();

    private (IReadOnlyList<CatalogEntry>, string?) Load(IReadOnlyList<string> names, IAppLog? log)
    {
        var entries = new List<CatalogEntry>();
        var skipped = new List<string>();
        foreach (var name in names.Where(n => n.StartsWith(EntryPrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var label = name[EntryPrefix.Length..^".json".Length];
            try
            {
                using var stream = _open(name);
                if (stream is null) { skipped.Add(label); continue; }
                using var document = JsonDocument.Parse(stream);
                var errors = CatalogValidation.Validate(document.RootElement);
                var root = document.RootElement;
                var script = errors.Count == 0 ? root.GetProperty("Builder").GetProperty("ScriptPath").GetString() ?? "" : "";
                if (errors.Count == 0 && !BuilderNameRegex().IsMatch(script)) errors.Add("Builder.ScriptPath must name a builder script file");
                if (errors.Count > 0)
                {
                    log?.Warning(LogArea, $"A catalog entry failed validation with {errors.Count} errors.");
                    skipped.Add(label);
                    continue;
                }
                var resource = BuilderPrefix + script;
                using var builder = _open(resource);
                if (builder is null) { skipped.Add(label); continue; }
                using var copy = new MemoryStream();
                builder.CopyTo(copy);
                entries.Add(ToEntry(root, resource, Sha256(copy.ToArray())));
            }
            catch (JsonException)
            {
                log?.Warning(LogArea, "A catalog entry could not be read as JSON.");
                skipped.Add(label);
            }
        }
        var duplicates = entries.GroupBy(e => e.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        if (duplicates.Count > 0)
        {
            skipped.AddRange(duplicates);
            entries.RemoveAll(e => duplicates.Contains(e.Id));
        }
        var ordered = entries.OrderBy(e => e.Subcategory, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        log?.Info(LogArea, $"Loaded {ordered.Count} catalog entries; {skipped.Count} skipped.");
        var message = skipped.Count == 0
            ? null
            : $"{skipped.Count.ToString(CultureInfo.InvariantCulture)} catalog {(skipped.Count == 1 ? "entry" : "entries")} did not load: {string.Join(", ", skipped)}.";
        return (ordered, message);
    }

    private static CatalogEntry ToEntry(JsonElement root, string builderResource, string sha256)
    {
        var builder = root.GetProperty("Builder");
        var os = root.GetProperty("OsScope");
        return new CatalogEntry(
            Text(root, "Id"), Text(root, "Version"), Text(root, "DisplayName"), Text(root, "Subcategory"), Text(root, "Status"),
            Text(root, "Description"), Text(root, "Severity"),
            Strings(root.GetProperty("Cves")),
            root.GetProperty("TenablePlugins").EnumerateArray().Select(e => e.GetInt32()).ToList(),
            Strings(root.GetProperty("KbReferences")),
            Strings(os.GetProperty("Client")), Strings(os.GetProperty("Server")),
            root.GetProperty("RebootRequired").GetBoolean(),
            root.GetProperty("RegistrySurface").EnumerateArray()
                .Select(r => new RegistrySurfaceItem(Text(r, "Hive"), Text(r, "KeyPath"), Strings(r.GetProperty("Values")))).ToList(),
            builder.GetProperty("Parameters").EnumerateArray().Select(p => new CatalogParameter(
                Text(p, "Name"), Text(p, "Type"), p.GetProperty("Required").GetBoolean(), DefaultText(p.GetProperty("Default")), Text(p, "Description"))).ToList(),
            root.GetProperty("DependsOn").EnumerateArray().Select(d => new CatalogDependency(
                Text(d, "Type"), Text(d, "Name"), d.GetProperty("CreatedBy").ValueKind == JsonValueKind.String ? d.GetProperty("CreatedBy").GetString() : null)).ToList(),
            Strings(root.GetProperty("Produces").GetProperty("ConfigurationItems")),
            Strings(root.GetProperty("Produces").GetProperty("ConfigurationBaselines")),
            builderResource, sha256,
            !root.TryGetProperty("RemediationSupported", out var remediationSupported) || remediationSupported.GetBoolean());
    }

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";

    private static IReadOnlyList<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString() ?? "").ToList();

    private static string? DefaultText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };
}

/// <summary>The catalog entry schema checks, with the messages of the catalog schema.</summary>
public static partial class CatalogValidation
{
    private static readonly string[] RequiredKeys =
    [
        "Id", "Version", "DisplayName", "Subcategory", "Mechanism", "Status", "Description", "Severity", "Cves", "TenablePlugins",
        "KbReferences", "OsScope", "RebootRequired", "RegistrySurface", "Builder", "DependsOn", "Produces"
    ];

    private static readonly string[] ParameterTypes = ["String", "Switch", "Int", "StringArray"];

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(\.\d+)?$")]
    private static partial Regex VersionRegex();

    /// <summary>The schema errors of one entry; empty when it is valid.</summary>
    public static List<string> Validate(JsonElement m)
    {
        var errors = new List<string>();
        if (m.ValueKind != JsonValueKind.Object)
        {
            errors.Add("The entry must be an object");
            return errors;
        }
        foreach (var key in RequiredKeys)
            if (!m.TryGetProperty(key, out _)) errors.Add($"Missing required key: {key}");

        foreach (var key in new[] { "Id", "Version", "DisplayName", "Subcategory", "Mechanism", "Status", "Description", "Severity" })
            if (m.TryGetProperty(key, out var v) && !IsNonEmptyString(v)) errors.Add($"{key} must be a non-empty string");
        StringArray(m, "Cves", errors);
        StringArray(m, "KbReferences", errors);
        if (m.TryGetProperty("TenablePlugins", out var plugins))
        {
            if (plugins.ValueKind != JsonValueKind.Array) errors.Add("TenablePlugins must be an array (int[])");
            else if (plugins.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out _))) errors.Add("TenablePlugins must contain only integers");
        }
        if (m.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String && !KebabRegex().IsMatch(id.GetString()!))
            errors.Add($"Id must be kebab-case (lowercase letters, digits, hyphens): '{id.GetString()}'");
        if (m.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.String && !VersionRegex().IsMatch(version.GetString()!))
            errors.Add($"Version must be semver (e.g. 1.0.0): '{version.GetString()}'");
        if (m.TryGetProperty("RebootRequired", out var reboot) && reboot.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            errors.Add("RebootRequired must be a boolean");
        if (m.TryGetProperty("RemediationSupported", out var remediation) && remediation.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            errors.Add("RemediationSupported must be a boolean");

        if (m.TryGetProperty("OsScope", out var os))
        {
            if (os.ValueKind != JsonValueKind.Object) errors.Add("OsScope must be a hashtable");
            else
                foreach (var sk in new[] { "Client", "Server" })
                {
                    if (!os.TryGetProperty(sk, out var list)) errors.Add($"OsScope.{sk} is required");
                    else if (list.ValueKind != JsonValueKind.Array) errors.Add($"OsScope.{sk} must be an array (string[])");
                    else if (list.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)) errors.Add($"OsScope.{sk} must contain only strings");
                }
        }

        if (m.TryGetProperty("RegistrySurface", out var rs))
        {
            if (rs.ValueKind != JsonValueKind.Array) errors.Add("RegistrySurface must be an array");
            else
            {
                var i = 0;
                foreach (var item in rs.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { errors.Add($"RegistrySurface[{i}] must be a hashtable"); i++; continue; }
                    if (!item.TryGetProperty("Hive", out var hive) || !IsNonEmptyString(hive)) errors.Add($"RegistrySurface[{i}].Hive must be a non-empty string");
                    if (!item.TryGetProperty("KeyPath", out var keyPath) || !IsNonEmptyString(keyPath)) errors.Add($"RegistrySurface[{i}].KeyPath must be a non-empty string");
                    if (!item.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Array) errors.Add($"RegistrySurface[{i}].Values must be an array (string[])");
                    else if (values.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)) errors.Add($"RegistrySurface[{i}].Values must contain only strings");
                    i++;
                }
            }
        }

        if (m.TryGetProperty("Builder", out var b))
        {
            if (b.ValueKind != JsonValueKind.Object) errors.Add("Builder must be a hashtable");
            else
            {
                if (!b.TryGetProperty("ScriptPath", out var sp) || !IsNonEmptyString(sp)) errors.Add("Builder.ScriptPath must be a non-empty string");
                if (!b.TryGetProperty("Parameters", out var ps) || ps.ValueKind != JsonValueKind.Array) errors.Add("Builder.Parameters must be an array");
                else
                {
                    var i = 0;
                    foreach (var p in ps.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.Object) { errors.Add($"Builder.Parameters[{i}] must be a hashtable"); i++; continue; }
                        if (!p.TryGetProperty("Name", out var name) || !IsNonEmptyString(name)) errors.Add($"Builder.Parameters[{i}].Name must be a non-empty string");
                        if (!p.TryGetProperty("Type", out var type) || type.ValueKind != JsonValueKind.String || !ParameterTypes.Contains(type.GetString()))
                            errors.Add($"Builder.Parameters[{i}].Type must be one of: {string.Join(", ", ParameterTypes)}");
                        if (!p.TryGetProperty("Required", out var required) || required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            errors.Add($"Builder.Parameters[{i}].Required must be a boolean");
                        if (!p.TryGetProperty("Default", out _)) errors.Add($"Builder.Parameters[{i}].Default key is required (may be $null)");
                        if (!p.TryGetProperty("Description", out var description) || description.ValueKind != JsonValueKind.String)
                            errors.Add($"Builder.Parameters[{i}].Description must be a string");
                        i++;
                    }
                }
            }
        }

        if (m.TryGetProperty("DependsOn", out var dep))
        {
            if (dep.ValueKind != JsonValueKind.Array) errors.Add("DependsOn must be an array");
            else
            {
                var i = 0;
                foreach (var d in dep.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) { errors.Add($"DependsOn[{i}] must be a hashtable"); i++; continue; }
                    if (!d.TryGetProperty("Type", out var type) || !IsNonEmptyString(type)) errors.Add($"DependsOn[{i}].Type must be a non-empty string");
                    if (!d.TryGetProperty("Name", out var name) || !IsNonEmptyString(name)) errors.Add($"DependsOn[{i}].Name must be a non-empty string");
                    if (!d.TryGetProperty("CreatedBy", out _)) errors.Add($"DependsOn[{i}].CreatedBy key is required (may be $null)");
                    i++;
                }
            }
        }

        if (m.TryGetProperty("Produces", out var pr))
        {
            if (pr.ValueKind != JsonValueKind.Object) errors.Add("Produces must be a hashtable");
            else
                foreach (var pk in new[] { "ConfigurationItems", "ConfigurationBaselines" })
                {
                    if (!pr.TryGetProperty(pk, out var list)) errors.Add($"Produces.{pk} is required");
                    else if (list.ValueKind != JsonValueKind.Array) errors.Add($"Produces.{pk} must be an array (string[])");
                    else if (list.GetArrayLength() < 1) errors.Add($"Produces.{pk} must list at least one artifact");
                    else if (list.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)) errors.Add($"Produces.{pk} must contain only strings");
                }
        }
        return errors;
    }

    private static bool IsNonEmptyString(JsonElement v) => v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());

    private static void StringArray(JsonElement m, string name, List<string> errors)
    {
        if (!m.TryGetProperty(name, out var v)) return;
        if (v.ValueKind != JsonValueKind.Array) errors.Add($"{name} must be an array (string[])");
        else if (v.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String)) errors.Add($"{name} must contain only strings");
    }
}
