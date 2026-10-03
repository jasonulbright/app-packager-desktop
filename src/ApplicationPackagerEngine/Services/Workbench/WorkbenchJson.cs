using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// The result of a dotted-path read. Found stays false for an absent key, so an
/// explicit null value remains distinguishable from inheritance.
/// </summary>
public readonly record struct WorkbenchMember(bool Found, JsonNode? Value)
{
    public static readonly WorkbenchMember Missing = new(false, null);

    /// <summary>The value as text: null for a JSON null or a missing member.</summary>
    public string? Text => WorkbenchJson.AsText(Value);
}

/// <summary>
/// JSON primitives for the workbench store: sparse objects whose keys compare
/// case-insensitively, dotted-path member access, and atomic file replacement.
/// </summary>
public static class WorkbenchJson
{
    public static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    public static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static JsonObject NewObject() => new(NodeOptions);

    public static JsonNode? Parse(string json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json, NodeOptions);

    /// <summary>A deep copy whose objects keep case-insensitive keys.</summary>
    public static JsonNode? Clone(JsonNode? node) => node is null ? null : Parse(node.ToJsonString());

    public static JsonObject CloneObject(JsonObject source) => (JsonObject)Clone(source)!;

    /// <summary>
    /// Reads a dotted path. A segment that meets a non-object, or an object
    /// without the key, reports not found.
    /// </summary>
    public static WorkbenchMember Get(JsonNode? root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj) return WorkbenchMember.Missing;
            if (!obj.TryGetPropertyValue(segment, out var next)) return WorkbenchMember.Missing;
            current = next;
        }
        return new WorkbenchMember(true, current);
    }

    /// <summary>Writes a dotted path, replacing any non-object on the way with a new object.</summary>
    public static void Set(JsonObject root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!current.TryGetPropertyValue(segments[i], out var next) || next is not JsonObject child)
            {
                child = NewObject();
                current[segments[i]] = child;
            }
            current = child;
        }
        current[segments[^1]] = value;
    }

    /// <summary>Removes the key at a dotted path. A missing parent leaves the object unchanged.</summary>
    public static void Remove(JsonObject root, string path)
    {
        var segments = path.Split('.');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!current.TryGetPropertyValue(segments[i], out var next) || next is not JsonObject child) return;
            current = child;
        }
        current.Remove(segments[^1]);
    }

    /// <summary>
    /// String coercion with PowerShell [string] semantics for scalars and arrays:
    /// an array joins its elements with a space, so an empty array is the empty string.
    /// </summary>
    public static string? AsText(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                var element = value.GetValueKind();
                return element switch
                {
                    JsonValueKind.String => value.GetValue<string>(),
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Null => null,
                    _ => value.ToJsonString()
                };
            case JsonArray array:
                return string.Join(" ", array.Select(item => AsText(item) ?? ""));
            default:
                return node.ToJsonString();
        }
    }

    /// <summary>PowerShell truthiness of a value: null, false, 0, "" and an empty array are false.</summary>
    public static bool IsTruthy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonArray array:
                return array.Count switch
                {
                    0 => false,
                    1 => IsTruthy(array[0]),
                    _ => true
                };
            case JsonObject:
                return true;
            case JsonValue value:
                return value.GetValueKind() switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False or JsonValueKind.Null => false,
                    JsonValueKind.String => value.GetValue<string>().Length > 0,
                    JsonValueKind.Number =>
                        double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        && number != 0,
                    _ => true
                };
            default:
                return true;
        }
    }

    /// <summary>Reads a whole-number value from a number or a numeric string; null when it is neither.</summary>
    public static int? AsInt(JsonNode? node)
    {
        var text = AsText(node);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public static JsonNode? Read(string path)
    {
        if (!File.Exists(path)) return null;
        return Parse(ReadText(path));
    }

    /// <summary>The whole file; a sharing violation from a writer or reader holding it for a moment is retried briefly.</summary>
    public static string ReadText(string path) => FileRetry.ReadAllText(path);

    public static void Write(string path, JsonNode? node) =>
        WriteText(path, node is null ? "null" : node.ToJsonString(FileOptions));

    public static void Write<T>(string path, T value) =>
        WriteText(path, JsonSerializer.Serialize(value, FileOptions));

    /// <summary>
    /// Replaces the file atomically: the new content lands in a side file and one rename
    /// moves it over the target, so a crash leaves either the old or the new file and a
    /// reader in another process never finds the name absent.
    /// </summary>
    public static void WriteText(string path, string text) =>
        WriteText(path, text, (temp, target, _) => FileRetry.MoveReplacing(temp, target));

    /// <summary>
    /// ReplaceFile can fail after it has renamed the original to the backup
    /// name and before the new content takes the original name. The backup is
    /// then the only copy of the original, so it is moved back, and neither it
    /// nor the side file is deleted while the target name is empty.
    /// </summary>
    internal static void WriteText(string path, string text, Action<string, string, string> replace)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var backup = $"{path}.{Guid.NewGuid():N}.bak";
        File.WriteAllText(temp, text, Utf8NoBom);
        try
        {
            if (File.Exists(path)) replace(temp, path, backup);
            else File.Move(temp, path);
        }
        catch
        {
            if (!File.Exists(path) && File.Exists(backup))
            {
                try { File.Move(backup, path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (File.Exists(path)) TryDelete(temp);
            throw;
        }
        finally
        {
            if (File.Exists(path)) TryDelete(backup);
        }
    }

    /// <summary>
    /// Writes a new file and never replaces one: false when the path already
    /// exists, including when another writer created it first.
    /// </summary>
    public static bool TryWriteNew<T>(string path, T value)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, FileOptions), Utf8NoBom);
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            TryDelete(temp);
            return false;
        }
    }

    public static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string TextSha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>True when the path itself carries the ReparsePoint attribute (symbolic link, junction or mount point).</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string NowRoundTrip() => DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
