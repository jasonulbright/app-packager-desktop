using System.Globalization;
using System.Text.Json;

namespace ApplicationPackagerEngine.Services;

/// <summary>The states of one read set after a scan.</summary>
internal static class HygieneSetStates
{
    public const string Read = "Read";
    public const string Cut = "Cut";
    public const string Failed = "Failed";
    public const string NotRead = "NotRead";
}

/// <summary>One read set: the columns in the order the script wrote them and each row as text.</summary>
internal sealed class HygieneTable
{
    private readonly Dictionary<string, int> _index;

    public HygieneTable(string name, IReadOnlyList<string> columns)
    {
        Name = name;
        Columns = columns;
        _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++) _index.TryAdd(columns[i], i);
    }

    public string Name { get; }
    public IReadOnlyList<string> Columns { get; }
    public List<string[]> Rows { get; } = [];
    public string State { get; set; } = HygieneSetStates.NotRead;
    public int Total { get; set; }
    public string Failure { get; set; } = "";

    /// <summary>A per-object set: the objects read; -1 for a bulk set.</summary>
    public int Done { get; set; } = -1;

    /// <summary>The rows as records with column access by name.</summary>
    public IEnumerable<HygieneRow> Records => Rows.Select(r => new HygieneRow(this, r));

    internal int Index(string column) => _index.TryGetValue(column, out var i) ? i : -1;

    public static HygieneTable Empty(string name, params string[] columns) => new(name, columns) { State = HygieneSetStates.Read };
}

/// <summary>One row of a read set. A missing column reads as empty text.</summary>
internal readonly struct HygieneRow(HygieneTable table, string[] values)
{
    public string S(string column)
    {
        if (table is null || values is null) return "";
        var i = table.Index(column);
        return i >= 0 && i < values.Length ? values[i] ?? "" : "";
    }

    public int I(string column, int fallback = 0) => int.TryParse(S(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    public long L(string column, long fallback = 0) => long.TryParse(S(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    public bool B(string column) => string.Equals(S(column), "True", StringComparison.OrdinalIgnoreCase) || S(column) == "1";

    /// <summary>The clock value the provider returned; null when the value is empty or not a time.</summary>
    public DateTime? T(string column) => HygieneSiteRead.Time(S(column));

    /// <summary>A value the script joined with '|' (an array of the provider).</summary>
    public string[] List(string column) => S(column) is { Length: > 0 } s ? s.Split('|') : [];
}

/// <summary>What one or more runs of the Site Hygiene script returned, parsed. Unknown kinds are ignored.</summary>
internal sealed class HygieneSiteRead
{
    public Dictionary<string, HygieneTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (int Done, int Total)> Coverage { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string SiteCode { get; set; } = "";
    public int Queries { get; set; }
    public bool? StateRead { get; set; }
    public string StateFailure { get; set; } = "";
    public List<(string Status, string Failure)> Applied { get; } = [];

    /// <summary>True when the script found the session on another site or provider than the caller named, and did nothing.</summary>
    public bool OtherConnection { get; set; }

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss";

    public static DateTime? Time(string text) =>
        DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public HygieneTable Table(string name) => Tables.TryGetValue(name, out var t) ? t : new HygieneTable(name, []);

    /// <summary>True when the set was read completely.</summary>
    public bool IsRead(string name) => Tables.TryGetValue(name, out var t) && t.State == HygieneSetStates.Read;

    /// <summary>Adds the sets of another run; a set read again replaces the earlier one.</summary>
    public void Merge(HygieneSiteRead other)
    {
        foreach (var (k, v) in other.Tables) Tables[k] = v;
        foreach (var (k, v) in other.Coverage) Coverage[k] = v;
        if (other.SiteCode.Length > 0) SiteCode = other.SiteCode;
        Queries += other.Queries;
        if (other.StateRead is not null) { StateRead = other.StateRead; StateFailure = other.StateFailure; }
        Applied.AddRange(other.Applied);
        OtherConnection |= other.OtherConnection;
    }

    public static HygieneSiteRead Parse(IReadOnlyList<JsonElement> output)
    {
        var read = new HygieneSiteRead();
        var pending = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in output)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("Kind", out var k) || k.ValueKind != JsonValueKind.String) continue;
            switch (k.GetString())
            {
                case "Site":
                    read.SiteCode = S(e, "Code").Trim().ToUpperInvariant();
                    break;
                case "Rows":
                {
                    var set = S(e, "Set");
                    if (!pending.TryGetValue(set, out var list)) pending[set] = list = [];
                    if (e.TryGetProperty("Rows", out var rows)) list.AddRange(RowsOf(rows));
                    break;
                }
                case "Set":
                {
                    var set = S(e, "Set");
                    var columns = e.TryGetProperty("Columns", out var c) ? Strings(c).ToList() : [];
                    var table = new HygieneTable(set, columns)
                    {
                        State = S(e, "State") switch
                        {
                            "Read" => HygieneSetStates.Read,
                            "Cut" => HygieneSetStates.Cut,
                            _ => HygieneSetStates.Failed
                        },
                        Total = I(e, "Total"),
                        Failure = S(e, "Failure")
                    };
                    if (pending.Remove(set, out var rows)) table.Rows.AddRange(rows);
                    if (e.TryGetProperty("Shown", out var shown)
                        && (shown.ValueKind != JsonValueKind.Number || !shown.TryGetInt32(out var count) || table.Rows.Count != count))
                    {
                        table.State = HygieneSetStates.Failed;
                        table.Failure = "row-count";
                    }
                    read.Tables[set] = table;
                    break;
                }
                case "Coverage":
                    read.Coverage[S(e, "Set")] = (I(e, "Done"), I(e, "Total"));
                    break;
                case "Queries":
                    read.Queries += I(e, "Count");
                    break;
                case "StateRead":
                    read.StateRead = B(e, "Read");
                    read.StateFailure = S(e, "Failure");
                    break;
                case "Applied":
                    read.Applied.Add((S(e, "Status"), S(e, "Failure")));
                    break;
                case "Connection":
                    if (!B(e, "Same")) read.OtherConnection = true;
                    break;
            }
        }
        foreach (var (set, _) in pending)
            if (!read.Tables.ContainsKey(set)) read.Tables[set] = new HygieneTable(set, []) { State = HygieneSetStates.Failed, Failure = "no-status" };
        foreach (var (set, (done, total)) in read.Coverage)
            if (read.Tables.TryGetValue(set, out var t))
            {
                t.Done = done;
                t.Total = total;
                if (done < total && t.State == HygieneSetStates.Read) t.State = HygieneSetStates.Cut;
            }
        return read;
    }

    private static IEnumerable<string[]> RowsOf(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array) yield break;
        // Windows PowerShell 5.1 writes a block of one row as that row, not as an array of rows.
        if (rows.GetArrayLength() > 0 && rows[0].ValueKind != JsonValueKind.Array)
        {
            yield return Strings(rows).ToArray();
            yield break;
        }
        foreach (var r in rows.EnumerateArray())
            if (r.ValueKind == JsonValueKind.Array) yield return Strings(r).ToArray();
    }

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => e.EnumerateArray().Select(Text),
        JsonValueKind.String => [e.GetString() ?? ""],
        _ => []
    };

    private static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => ""
    };

    private static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? Text(v) : "";

    private static bool B(JsonElement e, string name) => e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "True", StringComparison.OrdinalIgnoreCase)));

    private static int I(JsonElement e, string name) => int.TryParse(S(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
