using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Scrapes a JSON API endpoint and extracts a version string using a simple
/// path-based query expression. Covers apps whose version info comes from
/// structured JSON APIs (dotnet releases-index, Edge updates, endoflife.date, etc.).
///
/// Query syntax (in the Pattern field):
///   property                     — object property access
///   [N]                          — array index access
///   [?key=value]                 — first array element where key equals value
///   [?key!=value]                — first array element where key does NOT equal value
///   [?k1=v1,k2=v2]              — first element matching ALL conditions (AND)
///   [max:field]                  — array element whose field holds the highest version
///   property[?filter].field      — chained navigation
///
/// Examples:
///   releases-index[?channel-version=8.0].latest-runtime
///   [?Product=Stable].Releases[?Architecture=x64,Platform=Windows].ProductVersion
///   [?lts!=false].version
///   [?cycle=13].latest
///   [0].latest
/// </summary>
public sealed class JsonQueryScraper : IVersionScraper
{
    public string Type => "JsonQuery";

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return null;

        var json = await FetchJsonAsync(config.Url, ct);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = EvaluateQuery(doc.RootElement, config.Pattern);
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches JSON and evaluates both the version pattern and optional DownloadUrlQuery in a single request.
    /// </summary>
    public async Task<(string? Version, string? DownloadUrl)> GetVersionAndDownloadUrlAsync(
        VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return (null, null);

        var json = await FetchJsonAsync(config.Url, ct);
        if (json is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var version = EvaluateQuery(doc.RootElement, config.Pattern);
            string? downloadUrl = null;
            if (!string.IsNullOrEmpty(config.DownloadUrlQuery))
                downloadUrl = EvaluateQuery(doc.RootElement, config.DownloadUrlQuery);
            return (version, downloadUrl);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Evaluates a query path against a JSON element, returning the final string value.
    /// </summary>
    internal static string? EvaluateQuery(JsonElement root, string query)
    {
        var current = root;
        var segments = ParseSegments(query);

        foreach (var segment in segments)
        {
            if (segment.Property is not null)
            {
                if (current.ValueKind != JsonValueKind.Object)
                    return null;
                if (!current.TryGetProperty(segment.Property, out var prop))
                    return null;
                current = prop;
            }

            if (segment.ArrayAccess is not null)
            {
                if (current.ValueKind != JsonValueKind.Array)
                    return null;

                current = segment.ArrayAccess switch
                {
                    IndexAccess idx => GetByIndex(current, idx.Index),
                    FilterAccess filter => FindFirstMatch(current, filter.Conditions),
                    MaxVersionAccess max => FindHighestVersion(current, max.Field),
                    _ => throw new InvalidOperationException()
                };

                if (current.ValueKind == JsonValueKind.Undefined)
                    return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => current.GetRawText()
        };
    }

    private static JsonElement GetByIndex(JsonElement array, int index)
    {
        if (index < 0 || index >= array.GetArrayLength())
            return default;
        return array[index];
    }

    private static JsonElement FindFirstMatch(JsonElement array, List<FilterCondition> conditions)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (conditions.All(c => MatchesCondition(item, c)))
                return item;
        }
        return default;
    }

    /// <summary>
    /// Returns the array element whose <paramref name="field"/> holds the highest version.
    /// Ties keep the first occurrence.
    /// </summary>
    private static JsonElement FindHighestVersion(JsonElement array, string field)
    {
        JsonElement best = default;
        LooseVersion? bestVersion = null;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty(field, out var prop)) continue;

            var parsed = LooseVersion.TryParse(GetStringValue(prop));
            if (parsed is null) continue;

            if (bestVersion is null || parsed.CompareTo(bestVersion) > 0)
            {
                bestVersion = parsed;
                best = item;
            }
        }

        return best;
    }

    private static bool MatchesCondition(JsonElement obj, FilterCondition condition)
    {
        if (!obj.TryGetProperty(condition.Key, out var prop))
            return condition.IsNegated; // missing key: negated matches, positive doesn't

        var matches = condition.Value switch
        {
            "false" => prop.ValueKind == JsonValueKind.False,
            "true" => prop.ValueKind == JsonValueKind.True,
            "null" => prop.ValueKind == JsonValueKind.Null,
            _ => GetStringValue(prop) == condition.Value
        };

        return condition.IsNegated ? !matches : matches;
    }

    private static string? GetStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
    }

    // ── Query parser ────────────────────────────────────────────────

    private static List<QuerySegment> ParseSegments(string query)
    {
        var segments = new List<QuerySegment>();
        var remaining = query.AsSpan().Trim();

        while (remaining.Length > 0)
        {
            // Skip leading dot separator
            if (remaining[0] == '.')
                remaining = remaining[1..];

            if (remaining.Length == 0) break;

            string? property = null;
            IArrayAccess? arrayAccess = null;

            // Property name (up to next '.', '[', or end)
            if (remaining[0] != '[')
            {
                var end = remaining.IndexOfAny(['.', '[']);
                if (end < 0) end = remaining.Length;
                property = remaining[..end].ToString();
                remaining = remaining[end..];
            }

            // Array accessor immediately following property (or standalone)
            if (remaining.Length > 0 && remaining[0] == '[')
            {
                var close = remaining.IndexOf(']');
                if (close < 0) break; // malformed

                var inner = remaining[1..close].ToString();
                remaining = remaining[(close + 1)..];

                if (inner.StartsWith('?'))
                    arrayAccess = ParseFilter(inner[1..]);
                else if (inner.StartsWith("max:", StringComparison.OrdinalIgnoreCase))
                    arrayAccess = new MaxVersionAccess(inner[4..].Trim());
                else if (int.TryParse(inner, out var idx))
                    arrayAccess = new IndexAccess(idx);
            }

            if (property is not null || arrayAccess is not null)
                segments.Add(new QuerySegment(property, arrayAccess));
        }

        return segments;
    }

    private static FilterAccess ParseFilter(string filterExpr)
    {
        var conditions = new List<FilterCondition>();
        foreach (var part in filterExpr.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Contains("!="))
            {
                var kv = trimmed.Split("!=", 2);
                conditions.Add(new FilterCondition(kv[0].Trim(), kv[1].Trim(), IsNegated: true));
            }
            else if (trimmed.Contains('='))
            {
                var kv = trimmed.Split('=', 2);
                conditions.Add(new FilterCondition(kv[0].Trim(), kv[1].Trim(), IsNegated: false));
            }
        }
        return new FilterAccess(conditions);
    }

    // ── Types ───────────────────────────────────────────────────────

    private record QuerySegment(string? Property, IArrayAccess? ArrayAccess);
    private interface IArrayAccess;
    private record IndexAccess(int Index) : IArrayAccess;
    private record FilterAccess(List<FilterCondition> Conditions) : IArrayAccess;
    private record MaxVersionAccess(string Field) : IArrayAccess;
    private record FilterCondition(string Key, string Value, bool IsNegated);

    // ── HTTP fetch ──────────────────────────────────────────────────

    private static async Task<string?> FetchJsonAsync(string url, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = $"-L --silent --show-error --max-time 25 -A \"{UserAgent}\" \"{url}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(30), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        var stdout = await stdoutTask;
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }
}
