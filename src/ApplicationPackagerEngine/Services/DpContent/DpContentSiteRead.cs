using System.Globalization;
using System.Text.Json;

namespace ApplicationPackagerEngine.Services;

/// <summary>What one run of the DP content script returned, parsed. Unknown kinds are ignored.</summary>
internal sealed class DpContentSiteRead
{
    internal sealed record Dp(string Nal, string Name, string SiteCode, bool IsPullDp, string ResourceType);

    internal sealed record Content(string PackageId, string Name, string Type, double SizeKb, string ObjectId);

    internal sealed record Summary(string PackageId, string Nal, int State, int SourceVersion, DateTime? SummaryDate, int PackageType, string SiteCode);

    internal sealed record Message(string PackageId, string Nal, int MessageState, int MessageId, string Time);

    internal sealed record Instance(string PackageId, string Nal, int Status, bool RefreshNow, string LastRefresh);

    /// <param name="Status">requested, failed, stale or missing.</param>
    internal sealed record Applied(string PackageId, string Nal, string Status, string Failure, int StateNow, string BeforeRefresh, int BeforeMessage, string BeforeMessageTime);

    public List<Dp> Dps { get; } = [];
    public List<(string Nal, string Source)> PullSources { get; } = [];
    public List<Content> Contents { get; } = [];
    public List<Summary> Summaries { get; } = [];
    public List<Message> Messages { get; } = [];
    public List<Instance> Instances { get; } = [];
    public List<Applied> AppliedRows { get; } = [];
    public Dictionary<string, int> Deployments { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DeploymentsRead { get; private set; }
    public bool StateRead { get; private set; }
    public int TotalPairs { get; private set; } = -1;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss";

    public static DpContentSiteRead Parse(IReadOnlyList<JsonElement> output)
    {
        var read = new DpContentSiteRead();
        var nals = new Dictionary<int, string>();
        var packed = new List<(string Kind, JsonElement Rows)>();
        foreach (var e in output)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("Kind", out var k) || k.ValueKind != JsonValueKind.String) continue;
            switch (k.GetString())
            {
                case "Dp":
                    read.Dps.Add(new Dp(S(e, "Nal"), S(e, "Name"), S(e, "SiteCode"), B(e, "IsPullDp"), S(e, "ResourceType")));
                    break;
                case "PullSource":
                    read.PullSources.Add((S(e, "Nal"), S(e, "Source")));
                    break;
                case "Content":
                    read.Contents.Add(new Content(S(e, "PackageId").ToUpperInvariant(), S(e, "Name"), S(e, "Type"), D(e, "SizeKb"), S(e, "ObjectId")));
                    break;
                case "Nal":
                    nals[I(e, "Index")] = S(e, "Nal");
                    break;
                case "Pairs" or "Status" or "Instances":
                    if (e.TryGetProperty("Rows", out var rows)) packed.Add((k.GetString()!, rows.Clone()));
                    break;
                case "Applied":
                    read.AppliedRows.Add(new Applied(S(e, "PackageId").ToUpperInvariant(), S(e, "Nal"), S(e, "Status"), S(e, "Failure"), I(e, "StateNow", -1), S(e, "BeforeRefresh"),
                        I(e, "BeforeMessage", -1), S(e, "BeforeMessageTime")));
                    break;
                case "Deployments":
                    read.Deployments[S(e, "PackageId")] = I(e, "Count");
                    break;
                case "DeploymentsRead":
                    read.DeploymentsRead = B(e, "Read");
                    break;
                case "StateRead":
                    read.StateRead = B(e, "Read");
                    break;
                case "Total":
                    read.TotalPairs = I(e, "Pairs", -1);
                    break;
            }
        }
        foreach (var (kind, rows) in packed)
        {
            foreach (var text in Strings(rows))
            {
                var f = text.Split('\t');
                if (f.Length < 3 || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || !nals.TryGetValue(index, out var nal)) continue;
                var id = f[0].ToUpperInvariant();
                switch (kind)
                {
                    case "Pairs" when f.Length >= 7:
                        read.Summaries.Add(new Summary(id, nal, Int(f[2], -1), Int(f[3], 0), Time(f[4]), Int(f[5], -1), f[6]));
                        break;
                    case "Status" when f.Length >= 5:
                        read.Messages.Add(new Message(id, nal, Int(f[2], -1), Int(f[3], -1), f[4]));
                        break;
                    case "Instances" when f.Length >= 5:
                        read.Instances.Add(new Instance(id, nal, Int(f[2], -1), string.Equals(f[3], "True", StringComparison.OrdinalIgnoreCase), f[4]));
                        break;
                }
            }
        }
        return read;
    }

    private static IEnumerable<string> Strings(JsonElement rows) => rows.ValueKind switch
    {
        JsonValueKind.Array => rows.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString() ?? ""),
        JsonValueKind.String => [rows.GetString() ?? ""],
        _ => []
    };

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => ""
        } : "";

    private static bool B(JsonElement e, string name) => e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "True", StringComparison.OrdinalIgnoreCase)));

    private static int I(JsonElement e, string name, int fallback = 0) => Int(S(e, name), fallback);

    private static double D(JsonElement e, string name) => double.TryParse(S(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static int Int(string text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>The SummaryDate of the summarizer: the provider returns it in UTC with no offset (DMTF "+***").</summary>
    private static DateTime? Time(string text) =>
        DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
}
