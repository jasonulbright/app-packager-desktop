using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Turns the status rows of the site into device results. A CMPivot row carries its result as
/// &lt;result ResultCode="0" moreResults="False"&gt;&lt;e _i="0" Column="value" /&gt;&lt;/result&gt;; a Run Scripts
/// row carries the script output as text, often compact JSON. Values stay text; nothing in them is
/// run or followed.
/// </summary>
public static class PivotsResults
{
    /// <summary>The most result rows kept per device; the device result says when more arrived.</summary>
    public const int MaxRowsPerDevice = 10_000;

    /// <summary>The most characters of one value; a longer value is cut and ends with "...".</summary>
    public const int MaxValueLength = 32 * 1024;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = 64L * 1024 * 1024
    };

    /// <summary>The rows of a CMPivot result document, and whether the site says it holds more.</summary>
    public static (IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, bool More, bool Readable) PivotRows(string? output)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(output)) return (rows, false, true);
        var more = false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(output), XmlSettings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Depth == 0)
                {
                    more = string.Equals(reader.GetAttribute("moreResults"), "True", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (reader.Depth != 1) continue;
                if (rows.Count >= MaxRowsPerDevice) { more = true; break; }
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                if (reader.MoveToFirstAttribute())
                    do
                    {
                        if (reader.Name == "_i") continue;
                        row[reader.Name] = Cut(reader.Value);
                    } while (reader.MoveToNextAttribute());
                rows.Add(row);
            }
            return (rows, more, true);
        }
        catch (XmlException)
        {
            return (rows, more, false);
        }
    }

    /// <summary>The ResultCode of the root of a CMPivot result document; 0 when it names none or cannot be read.</summary>
    public static int ResultCode(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return 0;
        try
        {
            using var reader = XmlReader.Create(new StringReader(output), XmlSettings);
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element)
                    return int.TryParse(reader.GetAttribute("ResultCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : 0;
        }
        catch (XmlException)
        {
        }
        return 0;
    }

    /// <summary>The rows of a script output: a JSON object is one row, a JSON array of objects is one row each, other text is one row with an Output column.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ScriptRows(string? output)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(output)) return rows;
        var text = output.Trim();
        if (text[0] is '{' or '[')
        {
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                if (doc.RootElement.ValueKind == JsonValueKind.Object) { rows.Add(Flatten(doc.RootElement)); return rows; }
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (rows.Count >= MaxRowsPerDevice) break;
                        rows.Add(item.ValueKind == JsonValueKind.Object ? Flatten(item) : new Dictionary<string, string> { ["Output"] = Cut(Text(item)) });
                    }
                    return rows;
                }
            }
            catch (JsonException)
            {
            }
        }
        rows.Add(new Dictionary<string, string> { ["Output"] = Cut(output) });
        return rows;
    }

    private static Dictionary<string, string> Flatten(JsonElement o)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in o.EnumerateObject()) row[p.Name] = Cut(Text(p.Value));
        return row;
    }

    private static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => e.GetRawText()
    };

    /// <summary>
    /// The script output as the script wrote it. Run Scripts stores JSON output with its quotes
    /// escaped and no surrounding quotes ({\"Name\":\"x\"}); such text is read back as one JSON string.
    /// </summary>
    public static string ScriptOutputText(string? output)
    {
        var text = output ?? "";
        var trimmed = text.Trim();
        if (!(trimmed.StartsWith("{\\\"", StringComparison.Ordinal) || trimmed.StartsWith("[{\\\"", StringComparison.Ordinal) || trimmed.StartsWith("[\\\"", StringComparison.Ordinal))) return text;
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + trimmed + "\"") ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    internal static string Cut(string value) => value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "...";

    /// <summary>A UTC time from the site's JSON (2026-10-24T08:29:42.567Z) or the session (2026-10-24T08:29:42).</summary>
    internal static DateTime? Utc(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
}
