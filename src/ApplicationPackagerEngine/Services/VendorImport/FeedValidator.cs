using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.VendorImport;

public enum FindingSeverity { Blocking, Warning, Information }

/// <summary>One result of validation. A blocking finding keeps the definition out of the import.</summary>
public sealed record ImportFinding(FindingSeverity Severity, string Code, string Message, string? DefinitionId = null);

/// <summary>A definition of a feed after validation: the model, the re-serialized copy that is stored, and what the preview shows.</summary>
public sealed record ValidatedDefinition(
    string Id, AppDefinition Definition, string Json, IReadOnlyList<ImportFinding> Findings, IReadOnlyList<string> Hosts, IReadOnlyList<string> CodeProperties)
{
    public bool Blocked => Findings.Any(f => f.Severity == FindingSeverity.Blocking);
    public bool CarriesCode => CodeProperties.Count > 0;
}

/// <summary>The envelope of a feed and its definitions. The vendor and the FeedId are null when the document has no envelope.</summary>
public sealed record ValidatedFeed(
    string? Vendor, string? FeedId, DateTimeOffset? Updated, bool HasEnvelope,
    IReadOnlyList<ValidatedDefinition> Definitions, IReadOnlyList<ImportFinding> Findings)
{
    public bool Refused => Findings.Any(f => f.Severity == FindingSeverity.Blocking && f.DefinitionId is null);
}

/// <summary>
/// Strict validation of a feed document (new code; the existing loader checks no value). The document is parsed with no duplicate
/// property name (also of different letter case), every property must be one that the format knows, the identifiers, names and
/// addresses are checked, and a definition that carries code is blocked in every trust state. What is stored is the re-serialized model.
/// </summary>
public static partial class FeedValidator
{
    public const int MaxDefinitions = 200;
    public const int MaxDepth = 64;
    public const int MaxParsedBytes = 4 * 1024 * 1024;

    private static readonly string[] DeviceNames =
        ["con", "prn", "aux", "nul", "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
         "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"];

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex IdGrammar();

    [GeneratedRegex(@"^[a-z0-9-]{3,64}\z")]
    private static partial Regex FeedIdGrammar();

    /// <summary>The names that the shipped files carry and that the model does not read.</summary>
    private static readonly string[] AllowedUnmapped = ["Acquisition"];

    private static readonly string[] RefusedNames = ["ImportedFrom", "ImportedVendor"];

    public static bool IsDeviceName(string name) =>
        DeviceNames.Contains(name.Trim().TrimEnd('.', ' ').Split('.')[0], StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the text is a definition Id of the grammar (and so a safe file name).</summary>
    public static bool IsValidDefinitionId(string? id) =>
        id is not null && IdGrammar().IsMatch(id) && !id.All(c => c == '.') && !id.EndsWith('.') && !IsDeviceName(id);

    public static bool IsValidFeedId(string? feedId) =>
        feedId is not null && FeedIdGrammar().IsMatch(feedId) && !IsDeviceName(feedId);

    private static ImportFinding Block(string code, string message, string? id = null) => new(FindingSeverity.Blocking, code, message, id);

    public static ValidatedFeed Validate(byte[] document)
    {
        var findings = new List<ImportFinding>();
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(document, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException ex)
        {
            return Refuse($"The document is not valid JSON or is nested deeper than {MaxDepth} levels (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1}): {ex.Message}");
        }
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Refuse("The document is not a JSON object.");
            if (DuplicateName(root, "$") is { } duplicate) return Refuse(duplicate);

            List<JsonElement> definitions;
            string? vendor = null, feedId = null;
            DateTimeOffset? updated = null;
            var hasEnvelope = Property(root, "Definitions") is not null;
            if (hasEnvelope)
            {
                if (Property(root, "FeedFormat") is not { ValueKind: JsonValueKind.Number } format || !format.TryGetInt32(out var number) || number != 1)
                    return Refuse("The FeedFormat of the document is not 1: this program is too old or too new for it.");
                foreach (var name in new[] { "Vendor", "FeedId", "Updated" })
                    if (Property(root, name) is { } value && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        return Refuse($"The property '{name}' of the document is not text.");
                vendor = Property(root, "Vendor") is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
                feedId = Property(root, "FeedId") is { ValueKind: JsonValueKind.String } f ? f.GetString() : null;
                if (Property(root, "Updated") is { ValueKind: JsonValueKind.String } u && u.GetString() is { } text)
                {
                    if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when)) updated = when;
                    else findings.Add(Block("UpdatedInvalid", $"Updated '{text}' is not a date and time."));
                }
                if (Property(root, "Definitions") is not { ValueKind: JsonValueKind.Array } array)
                    return Refuse("Definitions is not a list.");
                definitions = array.EnumerateArray().ToList();
                if (definitions.Count > MaxDefinitions) return Refuse($"The document holds {definitions.Count} definitions; the limit is {MaxDefinitions}.");
                if (feedId is not null && !IsValidFeedId(feedId))
                    findings.Add(new ImportFinding(FindingSeverity.Warning, "FeedIdInvalid", $"FeedId '{feedId}' of the document is not 3 to 64 characters of a-z, 0-9 and '-', or it is a Windows device name. Choose another FeedId in the preview."));
            }
            else
            {
                // A single definition object without the envelope: the preview asks for the vendor name and the FeedId.
                definitions = [root];
            }
            if (document.Length > MaxParsedBytes) return Refuse("The document is larger than the limit after parse.");

            var results = new List<ValidatedDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in definitions)
            {
                ValidatedDefinition result;
                try
                {
                    result = element.ValueKind == JsonValueKind.Object
                        ? ValidateDefinition(element)
                        : new ValidatedDefinition("(not an object)", new AppDefinition(), "", [Block("Unreadable", "A definition of the list is not an object.", "(not an object)")], [], []);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or ArgumentException or FormatException)
                {
                    result = new ValidatedDefinition("(unreadable)", new AppDefinition(), "", [Block("Unreadable", $"The definition cannot be checked: {ex.Message}", "(unreadable)")], [], []);
                }
                if (!seen.Add(result.Id))
                    result = result with { Findings = [.. result.Findings, Block("IdDuplicate", $"The Id '{result.Id}' is used by more than one definition of the document.", result.Id)] };
                results.Add(result);
            }
            return new ValidatedFeed(vendor, feedId, updated, hasEnvelope, results, findings);
        }
    }

    private static ValidatedFeed Refuse(string message) =>
        new(null, null, null, false, [], [Block("DocumentRefused", message)]);

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    /// <summary>A property name that appears twice in one object, also with another letter case; the path of the first one found.</summary>
    private static string? DuplicateName(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        return $"The property '{property.Name}' appears twice in {path}. A document with a duplicate property name is refused, because the loaders of the program disagree about which value wins.";
                    if (DuplicateName(property.Value, $"{path}.{property.Name}") is { } inner) return inner;
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    if (DuplicateName(item, $"{path}[{index++}]") is { } innerItem) return innerItem;
                break;
        }
        return null;
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // An explicit null for a property that is not nullable is refused, not stored as a null that crashes a later step.
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static ValidatedDefinition ValidateDefinition(JsonElement element)
    {
        var id = Property(element, "Id") is { ValueKind: JsonValueKind.String } idElement ? idElement.GetString() ?? "(no Id)" : "(no Id)";
        var findings = new List<ImportFinding>();
        void Add(string code, string message) => findings.Add(Block(code, message, id));

        foreach (var name in RefusedNames)
            if (Property(element, name) is not null)
                Add("RefusedProperty", $"The property '{name}' is refused: the origin of a definition comes from the folder and the source record, never from the document.");

        // The names that the model does not read are removed before the strict read; the model rejects any other unknown name.
        var node = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())!.AsObject();
        foreach (var allowed in AllowedUnmapped)
            foreach (var key in node.Select(p => p.Key).Where(k => k.Equals(allowed, StringComparison.OrdinalIgnoreCase)).ToList())
                node.Remove(key);

        AppDefinition? definition = null;
        try
        {
            definition = node.Deserialize<AppDefinition>(ReadOptions);
        }
        catch (JsonException ex)
        {
            Add("Unreadable", $"The definition cannot be read; a property that the format does not know, or a value of the wrong kind: {ex.Message}");
        }
        if (definition is null)
            return new ValidatedDefinition(id, new AppDefinition(), "", findings, [], []);

        CheckIdentity(definition, Add);
        var hosts = new List<string>();
        CheckAddresses(definition, Add, hosts);
        var code = DefinitionCode.Find(definition);
        foreach (var name in code)
            findings.Add(Block("CarriesCode", $"The definition carries code ({name}). A definition that carries code is not imported in this release, whatever the trust state.", id));

        var json = definition is null ? "" : JsonSerializer.Serialize(definition, WriteOptions);
        return new ValidatedDefinition(definition.Id.Length > 0 ? definition.Id : id, definition, json, findings, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), code);
    }

    private static void CheckIdentity(AppDefinition d, Action<string, string> add)
    {
        if (!IdGrammar().IsMatch(d.Id) || d.Id.All(c => c == '.') || d.Id.EndsWith('.') || d.Id.EndsWith(' ') || IsDeviceName(d.Id))
            add("IdInvalid", $"The Id '{d.Id}' is not allowed: 1 to 64 characters of letters, digits, '.', '_' and '-', not only dots, not ending in a dot, and not a Windows device name.");
        foreach (var (field, value) in new[] { ("Application", d.Application), ("Vendor", d.Vendor), ("CMName", d.CMName) })
        {
            if (value.Length == 0) add("NameEmpty", $"{field} is empty.");
            else if (value.Length > 100) add("NameTooLong", $"{field} is longer than 100 characters.");
            else if (value.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(value) || value.Contains(':') || value.Contains('\\')
                     || IsDeviceName(value) || value.Any(char.IsControl))
                add("NameInvalid", $"{field} '{value}' is not a plain display name (no '..', no path, no drive colon, no device name).");
        }
        if (d.Architecture is not ("x64" or "x86" or "ARM64"))
            add("ArchitectureInvalid", $"Architecture '{d.Architecture}' is not x64, x86 or ARM64.");
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemePrefix();

    /// <summary>A relative reference that stays on the host: a path, a placeholder followed by a path, or a name.</summary>
    private static bool IsRelativeReference(string text) =>
        !text.StartsWith("//", StringComparison.Ordinal) && !text.StartsWith('\\') && !text.StartsWith("/\\", StringComparison.Ordinal)
        && !SchemePrefix().IsMatch(text) && !text.Contains("://", StringComparison.Ordinal);

    private static void CheckAddresses(AppDefinition d, Action<string, string> add, List<string> hosts)
    {
        void Check(string field, string? value, bool relativeAllowed = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var text = value.Trim();
            // A template holds placeholders such as {version}; the address is checked with a stand-in for each.
            var concrete = Regex.Replace(text, @"\{[^{}]*\}", "x");
            if (relativeAllowed && IsRelativeReference(text))
            {
                // A relative hop is resolved against the hop before it at run time. A form that leaves that host is refused here:
                // protocol-relative (//host), a backslash, or a scheme.
                return;
            }
            if (relativeAllowed && (text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith('\\') || text.StartsWith("/\\", StringComparison.Ordinal)
                                    || SchemePrefix().IsMatch(text)) && !text.Contains("://", StringComparison.Ordinal))
            { add("AddressNotHttps", $"{field} '{text}' is a relative form that leaves the host of the hop before it."); return; }
            // A placeholder in the scheme, the user information or the host part could form a foreign host.
            var afterScheme = text.IndexOf("://", StringComparison.Ordinal);
            if (afterScheme > 0)
            {
                var authorityEnd = text.IndexOfAny(['/', '?', '#'], afterScheme + 3);
                var authority = authorityEnd < 0 ? text[(afterScheme + 3)..] : text[(afterScheme + 3)..authorityEnd];
                if (text[..afterScheme].Contains('{') || authority.Contains('{'))
                { add("AddressPlaceholderInHost", $"{field} puts a placeholder in the scheme or the host, which could name another host."); return; }
            }
            if (concrete.StartsWith("\\\\", StringComparison.Ordinal) || concrete.StartsWith("//", StringComparison.Ordinal))
            { add("AddressNotHttps", $"{field} is a UNC path; every address must be https:."); return; }
            if (!Uri.TryCreate(concrete, UriKind.Absolute, out var uri))
            { add("AddressInvalid", $"{field} '{text}' is not an address."); return; }
            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            { add("AddressNotHttps", $"{field} uses '{uri.Scheme}'; every address must be https:."); return; }
            if (!string.IsNullOrEmpty(uri.UserInfo))
            { add("AddressUserInfo", $"{field} holds user information (user:password@host)."); return; }
            hosts.Add(uri.Host);
        }

        Check("VendorUrl", d.VendorUrl);
        Check("SecurityAdvisoryUrl", d.SecurityAdvisoryUrl);
        Check("DownloadUrl", d.DownloadUrl);
        Check("PayloadSha256Url", d.PayloadSha256Url);
        Check("VersionScraper.Url", d.VersionScraper?.Url);
        Check("VersionScraper.ServedUrlTemplate", d.VersionScraper?.ServedUrlTemplate);
        foreach (var (hop, index) in (d.VersionScraper?.Hops ?? []).Select((h, i) => (h, i)))
            Check($"VersionScraper.Hops[{index}].Url", hop.Url, relativeAllowed: index > 0);
        Check("BootstrapperLayout.BootstrapperUrl", d.BootstrapperLayout?.BootstrapperUrl);
        foreach (var (variant, index) in (d.PayloadVariants ?? []).Select((v, i) => (v, i)))
            Check($"PayloadVariants[{index}].DownloadUrl", variant.DownloadUrl);
    }
}

/// <summary>
/// The properties of a definition that run code or that name a file of the administrator. The classification covers every property of
/// the format: a test fails when the format gets a property that this class does not classify (see "Code in a definition").
/// </summary>
public static class DefinitionCode
{
    /// <summary>Every top-level property of <see cref="AppDefinition"/> that runs code or reads a local file, by name.</summary>
    public static readonly IReadOnlyCollection<string> CodeProperties =
    [
        "PostInstallScript", "UninstallCommand", "ExtractSetup", "BootstrapperLayout", "InstallerSettings",
        "LocalSource", "LocalInstaller", "SideFiles", "TargetSecret", "Odt", "ZipPayload"
    ];

    /// <summary>The top-level properties that hold no code (the nested values are checked by <see cref="Find"/>).</summary>
    public static readonly IReadOnlyCollection<string> OtherProperties =
    [
        "Schema", "Id", "WsusSupport", "Vendor", "Application", "CMName", "AppName", "Publisher", "Architecture", "VendorUrl", "Cpe",
        "ArpName", "ArpVersion", "ArpArch", "SecurityAdvisoryUrl", "InstallerType", "InstallerFramework", "InstallContext", "VersionScraper",
        "DownloadUrl", "InstallerFileName", "InstallArgs", "UninstallArgs", "ReviewBeforeStage", "CanUninstall", "Detection", "RunningProcess",
        "PostInstallKillProcesses", "RebootExitCodes", "SuccessExitCodes", "Icon", "IconFormat", "IconSource", "DisplayVersionOverride",
        "SupportsVariants", "PayloadVariants", "SupportsInstallModes", "CitrixSwitchStream", "PostExecutionBehavior", "ExpectedPayloadVersion",
        "PayloadSha256Url", "PayloadSha256", "OffersInstallModes", "RequirePayloadHash", "ExpandInstallArgs", "RequireValidSignature", "SourcePath", "SourceOrigin"
    ];

    /// <summary>The names of the top-level properties of the format that no list classifies; empty when the lists are complete.</summary>
    public static IReadOnlyList<string> Unclassified() =>
        typeof(AppDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => !CodeProperties.Contains(n) && !OtherProperties.Contains(n))
            .ToList();

    /// <summary>The code of one definition, by the name of the property (and where in it).</summary>
    public static IReadOnlyList<string> Find(AppDefinition d)
    {
        var found = new List<string>();
        if (d.PostInstallScript is not null) found.Add("PostInstallScript");
        if (!string.IsNullOrWhiteSpace(d.UninstallCommand)) found.Add("UninstallCommand");
        if (d.ExpandInstallArgs && !string.IsNullOrWhiteSpace(d.InstallArgs)) found.Add("InstallArgs with ExpandInstallArgs");
        if (d.ExtractSetup is not null) found.Add("ExtractSetup (ExtractArgs)");
        if (d.BootstrapperLayout is not null) found.Add("BootstrapperLayout");
        foreach (var setting in d.InstallerSettings ?? [])
        {
            if (!string.IsNullOrWhiteSpace(setting.CommandFragment)) found.Add($"InstallerSettings[{setting.Key}].CommandFragment");
            if (!string.IsNullOrWhiteSpace(setting.UninstallFragment)) found.Add($"InstallerSettings[{setting.Key}].UninstallFragment");
        }
        foreach (var variant in d.PayloadVariants ?? [])
            if (!string.IsNullOrWhiteSpace(variant.UninstallCommand)) found.Add($"PayloadVariants[{variant.NameSuffix}].UninstallCommand");
        if (d.LocalSource is not null) found.Add("LocalSource");
        if (d.LocalInstaller is not null) found.Add("LocalInstaller");
        if (d.SideFiles is { Length: > 0 }) found.Add("SideFiles");
        if (d.TargetSecret is not null) found.Add("TargetSecret");
        // The Office Deployment Tool configuration is built from these values without escaping and runs as SYSTEM; the zip payload
        // deletes a folder and a registry key that the document names.
        if (d.Odt is not null) found.Add("Odt");
        if (d.ZipPayload is not null) found.Add("ZipPayload");
        AddDetectionCode(d.Detection, "Detection", found);
        foreach (var variant in d.PayloadVariants ?? []) AddDetectionCode(variant.Detection, $"PayloadVariants[{variant.NameSuffix}].Detection", found);
        return found;
    }

    private static void AddDetectionCode(DetectionConfig? detection, string where, List<string> found)
    {
        if (detection is null) return;
        if (!string.IsNullOrWhiteSpace(detection.ScriptText) || detection.Type.Equals("Script", StringComparison.OrdinalIgnoreCase))
            found.Add($"{where}.ScriptText");
        foreach (var (clause, index) in (detection.Clauses ?? []).Select((c, i) => (c, i)))
            AddDetectionCode(clause, $"{where}.Clauses[{index}]", found);
    }
}
