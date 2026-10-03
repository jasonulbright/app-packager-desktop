using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// Reproduces the text Windows PowerShell 5.1 produces for
/// <c>ConvertTo-Json -Depth 12</c> over the output of the stage-manifest
/// canonicalizer, so a digest over that text matches one computed by a
/// PowerShell packager for the same content.
/// </summary>
/// <remarks>
/// The canonicalizer returns arrays through the PowerShell pipeline, which unrolls
/// them: an empty array becomes an AutomationNull that serializes as an empty
/// object, a one-element array becomes its element, and nested arrays flatten into
/// their parent. Object keys sort case-insensitively with culture rules, which
/// ignore hyphens and apostrophes at the first level. Formatting follows the 5.1
/// formatter: a member line is indented four columns past its container's opening
/// bracket, a colon is followed by two spaces, an empty container spans a blank
/// line, and lines end in CRLF. Strings are escaped as JavaScriptSerializer does.
/// Number tokens take the text of the .NET type ConvertFrom-Json gives them. Objects nested deeper than twelve levels are
/// not reproduced; a stage manifest does not reach that depth.
/// </remarks>
public static class PowerShellJson
{
    private const string NewLine = "\r\n";

    private abstract record Item;
    private sealed record Scalar(JsonNode? Value) : Item;
    private sealed record Map(List<KeyValuePair<string, Item>> Members) : Item;
    private sealed record List(List<Item> Items) : Item;
    private sealed record Nothing : Item;

    /// <summary>Canonicalizes a JSON tree and serializes it as Windows PowerShell 5.1 would.</summary>
    public static string SerializeCanonical(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, Assign(Collect(node)), 0);
        return builder.ToString();
    }

    private static List<Item> Collect(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var members = obj
                    .OrderBy(p => p.Key, KeyComparer.Instance)
                    .Select(p => new KeyValuePair<string, Item>(p.Key, Assign(Collect(p.Value))))
                    .ToList();
                return [new Map(members)];
            case JsonArray array:
                return array.SelectMany(Collect).ToList();
            default:
                return [new Scalar(node)];
        }
    }

    private static Item Assign(List<Item> output) => output.Count switch
    {
        0 => new Nothing(),
        1 => output[0],
        _ => new List(output)
    };

    private static void Write(StringBuilder builder, Item item, int column)
    {
        switch (item)
        {
            case Scalar scalar:
                builder.Append(ScalarText(scalar.Value));
                break;
            case Nothing:
                builder.Append('{').Append(NewLine).Append(NewLine).Append(' ', column).Append('}');
                break;
            case Map map when map.Members.Count == 0:
                builder.Append('{').Append(NewLine).Append(NewLine).Append(' ', column).Append('}');
                break;
            case List list when list.Items.Count == 0:
                builder.Append('[').Append(NewLine).Append(NewLine).Append(' ', column).Append(']');
                break;
            case Map map:
                builder.Append('{').Append(NewLine);
                for (var i = 0; i < map.Members.Count; i++)
                {
                    if (i > 0) builder.Append(',').Append(NewLine);
                    var key = Quote(map.Members[i].Key);
                    builder.Append(' ', column + 4).Append(key).Append(":  ");
                    Write(builder, map.Members[i].Value, column + 4 + key.Length + 3);
                }
                builder.Append(NewLine).Append(' ', column).Append('}');
                break;
            case List list:
                builder.Append('[').Append(NewLine);
                for (var i = 0; i < list.Items.Count; i++)
                {
                    if (i > 0) builder.Append(',').Append(NewLine);
                    builder.Append(' ', column + 4);
                    Write(builder, list.Items[i], column + 4);
                }
                builder.Append(NewLine).Append(' ', column).Append(']');
                break;
        }
    }

    private static string ScalarText(JsonNode? node)
    {
        if (node is not JsonValue value) return "null";
        return value.GetValueKind() switch
        {
            JsonValueKind.String => Quote(value.GetValue<string>()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Number => NumberText(value.ToJsonString()),
            _ => value.ToJsonString()
        };
    }

    /// <summary>
    /// A number token as Windows PowerShell 5.1 writes it back after
    /// ConvertFrom-Json: a token without a decimal point or exponent becomes
    /// Int32, then Int64; one without an exponent becomes Decimal, which keeps
    /// its scale; anything else becomes Double, written in .NET Framework
    /// round-trip form (15 significant digits when they round-trip, otherwise
    /// 17) with negative zero as 0.
    /// </summary>
    internal static string NumberText(string token)
    {
        var invariant = CultureInfo.InvariantCulture;
        if (token.IndexOf('e', StringComparison.OrdinalIgnoreCase) < 0)
        {
            if (token.IndexOf('.') < 0)
            {
                if (int.TryParse(token, NumberStyles.Integer, invariant, out var i)) return i.ToString(invariant);
                if (long.TryParse(token, NumberStyles.Integer, invariant, out var l)) return l.ToString(invariant);
            }
            if (decimal.TryParse(token, NumberStyles.Number, invariant, out var m))
            {
                var text = m.ToString(invariant);
                return m == 0m ? text.TrimStart('-') : text;
            }
        }
        if (!double.TryParse(token, NumberStyles.Float, invariant, out var d) || double.IsInfinity(d)) return token;
        if (d == 0) return "0";
        var shortForm = d.ToString("G15", invariant);
        return double.Parse(shortForm, NumberStyles.Float, invariant) == d ? shortForm : d.ToString("G17", invariant);
    }

    /// <summary>JavaScriptSerializer string escaping (HttpUtility.JavaScriptStringEncode).</summary>
    private static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < 0x20 || c is '\'' or '<' or '>' or '&' or (char)0x0085 or (char)0x2028 or (char)0x2029)
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>
    /// Case-insensitive culture ordering as Sort-Object applies it under Windows
    /// PowerShell 5.1: hyphens and apostrophes do not weigh at the first level.
    /// </summary>
    private sealed class KeyComparer : IComparer<string>
    {
        public static readonly KeyComparer Instance = new();
        private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

        int IComparer<string>.Compare(string? x, string? y)
        {
            var primary = Compare.Compare(Strip(x), Strip(y), CompareOptions.IgnoreCase);
            if (primary != 0) return primary;
            var secondary = Compare.Compare(x, y, CompareOptions.IgnoreCase);
            return secondary != 0 ? secondary : string.CompareOrdinal(x, y);
        }

        private static string Strip(string? value) =>
            (value ?? "").Replace("-", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal);
    }
}
